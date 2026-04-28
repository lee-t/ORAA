#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using OpenRA.API.Protos;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.API.Traits
{
	/// <summary>
	/// Trait info for the RL Interface. Attach this to the World actor
	/// to enable gRPC-based reinforcement learning control.
	/// </summary>
	public class RLInterfaceInfo : TraitInfo
	{
		/// <summary>The port on which the gRPC server will listen.</summary>
		public readonly int Port = 50051;

		public override object Create(ActorInitializer init) { return new RLInterface(init.Self, this); }
	}

	/// <summary>
	/// The RL Interface trait. Hosts a gRPC server that blocks the game loop
	/// each tick until an external client sends an action via the Step RPC.
	/// This enables synchronous, step-locked reinforcement learning.
	/// </summary>
	public class RLInterface : ITick, INotifyCreated
	{
		readonly RLInterfaceInfo info;
		readonly World world;

		Grpc.Core.Server grpcServer;

		// Synchronization primitives for step-locking the game loop.
		// The game tick blocks on actionReady, waiting for a client action.
		// Once the tick processes the action and builds the state, it signals tickComplete.
		readonly AutoResetEvent actionReady = new(false);
		readonly AutoResetEvent tickComplete = new(false);

		// Shared state between the gRPC service and the game tick.
		volatile Protos.Action pendingAction;
		volatile GameState latestState;
		volatile bool episodeDone;
		volatile bool resetRequested;

		public RLInterface(Actor self, RLInterfaceInfo info)
		{
			this.info = info;
			world = self.World;
		}

		void INotifyCreated.Created(Actor self)
		{
			if (!Game.IsHeadless)
			{
				Console.WriteLine("[ORAA] RLInterface trait loaded but headless mode is off. gRPC server will NOT start.");
				return;
			}

			Console.WriteLine($"[ORAA] Starting gRPC server on port {info.Port}...");

			var serviceImpl = new RlEnvironmentService(this);

			grpcServer = new Grpc.Core.Server
			{
				Services = { RlEnvironment.BindService(serviceImpl) },
				Ports = { new ServerPort("0.0.0.0", info.Port, ServerCredentials.Insecure) }
			};

			grpcServer.Start();
			Console.WriteLine($"[ORAA] gRPC server listening on port {info.Port}. Waiting for client...");
		}

		void ITick.Tick(Actor self)
		{
			if (!Game.IsHeadless || grpcServer == null)
				return;

			// Build the current game state snapshot.
			latestState = BuildGameState();
			episodeDone = world.IsGameOver;

			// Signal that we have a new state ready for the client.
			tickComplete.Set();

			// Block the game loop until the client sends the next action.
			// This is the core synchronization mechanism for RL training.
			actionReady.WaitOne();

			// Process the pending action.
			if (pendingAction != null)
			{
				ProcessAction(self, pendingAction);
				pendingAction = null;
			}
		}

		GameState BuildGameState()
		{
			var state = new GameState
			{
				Tick = world.WorldTick,
				Resources = 0
			};

			// Find the local player's resources.
			var localPlayer = world.LocalPlayer;
			if (localPlayer != null)
			{
				var playerResources = localPlayer.PlayerActor.TraitOrDefault<PlayerResources>();
				if (playerResources != null)
					state.Resources = playerResources.Cash + playerResources.Resources;
			}

			// Enumerate all actors in the world that have a position.
			foreach (var actor in world.Actors)
			{
				if (actor.IsDead || !actor.IsInWorld)
					continue;

				// Only serialize actors with the Mobile trait (units that can move).
				// This keeps the initial version simple and focused on unit movement.
				var mobile = actor.TraitOrDefault<Mobile>();
				if (mobile == null)
					continue;

				var health = actor.TraitOrDefault<IHealth>();
				var pos = actor.Location;

				var unitState = new UnitState
				{
					ActorId = actor.ActorID,
					Type = actor.Info.Name,
					Owner = actor.Owner.ResolvedPlayerName,
					Hp = health?.HP ?? 0,
					MaxHp = health?.MaxHP ?? 0,
					PosX = pos.X,
					PosY = pos.Y
				};

				state.Units.Add(unitState);
			}

			return state;
		}

		void ProcessAction(Actor self, Protos.Action action)
		{
			switch (action.Type)
			{
				case ActionType.ActionMove:
				{
					var target = world.GetActorById(action.ActorId);
					if (target == null || target.IsDead || !target.IsInWorld)
						break;

					var cell = new CPos(action.TargetX, action.TargetY);
					world.IssueOrder(new Order("Move", target, Target.FromCell(world, cell), false));
					break;
				}

				case ActionType.ActionStop:
				{
					var target = world.GetActorById(action.ActorId);
					if (target == null || target.IsDead || !target.IsInWorld)
						break;

					world.IssueOrder(new Order("Stop", target, false));
					break;
				}

				case ActionType.ActionNoop:
				default:
					break;
			}
		}

		/// <summary>
		/// The gRPC service implementation. Runs on the gRPC thread pool
		/// and communicates with the game thread via AutoResetEvents.
		/// </summary>
		sealed class RlEnvironmentService : RlEnvironment.RlEnvironmentBase
		{
			readonly RLInterface rl;

			public RlEnvironmentService(RLInterface rl)
			{
				this.rl = rl;
			}

			public override Task<ResetResponse> Reset(ResetRequest request, ServerCallContext context)
			{
				Console.WriteLine("[ORAA] Reset requested by client.");

				// For the initial implementation, Reset just waits for the first tick
				// to produce state. Full reset (restarting the game) will be added later.
				rl.resetRequested = true;

				// Wait for the first game tick to produce state.
				rl.tickComplete.WaitOne();

				var response = new ResetResponse
				{
					State = rl.latestState
				};

				return Task.FromResult(response);
			}

			public override Task<StepResponse> Step(StepRequest request, ServerCallContext context)
			{
				// Set the action for the game thread to process.
				rl.pendingAction = request.Action;

				// Signal the game thread that an action is ready.
				rl.actionReady.Set();

				// Wait for the game thread to process the action and produce new state.
				rl.tickComplete.WaitOne();

				var response = new StepResponse
				{
					State = rl.latestState,
					Done = rl.episodeDone,
					Reward = 0.0 // Reward shaping to be implemented later.
				};

				return Task.FromResult(response);
			}
		}
	}
}
