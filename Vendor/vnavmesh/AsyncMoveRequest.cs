using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace Navmesh;

public class AsyncMoveRequest : IDisposable
{
	private NavmeshManager _manager;
	private FollowPath _follow;
	private Task<List<Waypoint>>? _pendingTask;
	private bool _pendingFly;
	private float _pendingDestRange;

	public bool TaskInProgress => _pendingTask != null;

	public AsyncMoveRequest(NavmeshManager manager, FollowPath follow)
	{
		_manager = manager;
		_follow = follow;

		_follow.OnStuck += (dest, fly, range) =>
		{
			if (!Service.Config.RetryOnStuck)
				return;

			MoveTo(dest, fly, range);
		};
	}

	public void Dispose()
	{
		if (_pendingTask != null)
		{
			if (!_pendingTask.IsCompleted)
				_pendingTask.Wait();
			_pendingTask.Dispose();
			_pendingTask = null;
		}
	}

	// AutoTreasure change: a path request carries the generation it was made in.
	//
	// Stopping only cleared the waypoints, not the in-flight pathfinding task.
	// The task kept running and Update() handed its result straight to Move(),
	// so a path requested before a stop could revive movement afterwards:
	//
	//     request path to the old room
	//       -> room change detected, Stop()
	//       -> the old pathfinding finishes
	//       -> its result is used, and we walk back to the old room
	//
	// Cancellation alone does not close this: a task that completes just before
	// the cancel still delivers a result. So the generation is checked at the
	// moment the result would be used, which is the only point that matters.
	private int _generation;

	/// <summary>Invalidate every path request made so far.</summary>
	public void InvalidatePending() => ++_generation;

	private int _pendingGeneration;

	public void Update()
	{
		if (_pendingTask != null && _pendingTask.IsCompleted)
		{
			if (_pendingGeneration != _generation)
			{
				// Requested before a stop or a room change. Whatever it found
				// describes a situation we are no longer in.
				Service.Log.Debug($"Discarding stale path (gen {_pendingGeneration} != {_generation})");
				_pendingTask.Dispose();
				_pendingTask = null;
				return;
			}

			Service.Log.Information($"Pathfinding complete");
			try
			{
				_follow.Move(_pendingTask.Result, !_pendingFly, _pendingDestRange);
			}
			catch (Exception ex)
			{
				// vnavmesh 本体の画面表示つきログは取り込んでいないので、記録だけ残す。
				Service.Log.Error(ex, "Failed to find path");
			}
			_pendingTask.Dispose();
			_pendingTask = null;
		}
	}

	public bool MoveTo(Vector3 dest, bool fly, float range = 0)
	{
		if (_pendingTask != null)
		{
			Service.Log.Error($"Pathfinding task is in progress...");
			return false;
		}

		var toleranceStr = range > 0 ? $" within {range}y" : "";

		Service.Log.Info($"Queueing {(fly ? "fly" : "move")}-to {dest:f3}{toleranceStr}");
		_pendingTask = _manager.QueryPath(Service.ObjectTable.LocalPlayer?.Position ?? default, dest, fly, range: range);
		_pendingFly = fly;
		_pendingDestRange = range;
		_pendingGeneration = _generation;
		return true;
	}
}
