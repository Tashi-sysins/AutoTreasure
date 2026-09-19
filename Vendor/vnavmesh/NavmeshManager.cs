using Dalamud.Game.ClientState.Conditions;
using DotRecast.Detour;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

// manager that loads navmesh matching current zone and performs async pathfinding queries
public sealed class NavmeshManager : IDisposable
{
	public bool UseRaycasts = true;
	public bool UseStringPulling = true;

	public string CurrentKey { get; private set; } = ""; // unique string representing currently loaded navmesh
	public Navmesh? Navmesh { get; private set; }
	public NavmeshQuery? Query { get; private set; }
	public event Action<Navmesh?, NavmeshQuery?>? OnNavmeshChanged;

	private volatile float _loadTaskProgress = -1;
	public float LoadTaskProgress => _loadTaskProgress; // negative if load task is not running, otherwise in [0, 1] range

	private CancellationTokenSource? _currentCTS; // this is signalled when mesh is unloaded, all pathfinding tasks that use it are then cancelled
	private DateTime? _loadStuckSince; // when the mesh first looked stuck (null = fine)
	private int _stuckRetries; // how many times we retried for the current zone
	private const double StuckLoadSeconds = 15; // no mesh and no progress for this long = stuck
	private const int MaxStuckRetries = 3;

	private Task _lastLoadQueryTask; // we limit the concurrency to max 1 running task (otherwise we'd need multiple Query objects, which aren't lightweight); note that each task completes on main thread!

	private int _numActivePathfinds;
	public bool PathfindInProgress => _numActivePathfinds > 0;
	public int NumQueuedPathfindRequests => _numActivePathfinds > 0 ? _numActivePathfinds - 1 : 0;

	private DirectoryInfo _cacheDir;

	public unsafe NavmeshManager(DirectoryInfo cacheDir)
	{
		_cacheDir = cacheDir;
		cacheDir.Create(); // ensure directory exists

		// prepare a task with correct task scheduler that other tasks can be chained off
		_lastLoadQueryTask = Service.Framework.Run(() => Log("Tasks kicked off"));
	}

	public void Dispose()
	{
		Log("Disposing");
		ClearState();
	}

	public void Update()
	{
		WatchForStuckLoad();

		var curKey = GetCurrentKey();
		if (curKey != CurrentKey)
		{
			// navmesh needs to be reloaded
			if (!Service.Config.AutoLoadNavmesh)
			{
				if (CurrentKey.Length == 0)
					return; // nothing is loaded, and auto-load is forbidden
				curKey = ""; // just unload existing mesh
			}
			Log($"Starting transition from '{CurrentKey}' to '{curKey}'");
			CurrentKey = curKey;
			_loadStuckSince = null;
			_stuckRetries = 0;
			Reload(true);
			// mesh load is now in progress
		}
	}

	public bool Reload(bool allowLoadFromCache)
	{
		// The task chain can get poisoned: if an earlier queued task was cancelled
		// before it started running (Framework.Run with an already-cancelled token),
		// every task queued after it awaits a task that never completes, so the
		// navmesh load never runs and the mesh stays null forever.
		//
		// Observed in the wild: a client sits at "Mesh: Not Ready" indefinitely and
		// only recovers by zoning again. Reset the chain here so a reload always runs.
		ResetTaskChainIfStuck();

		ClearState();
		if (CurrentKey.Length > 0)
		{
			var cts = _currentCTS = new();
			ExecuteWhenIdle(async cancel =>
			{
				_loadTaskProgress = 0;

				using var resetLoadProgress = new OnDispose(() => _loadTaskProgress = -1);

				var waitStart = DateTime.Now;

				while (InCutscene)
				{
					if ((DateTime.Now - waitStart).TotalSeconds >= 5)
					{
						waitStart = DateTime.Now;
						Log("waiting for cutscene");
					}
					await Service.Framework.DelayTicks(1, cancel);
				}

				var (cacheKey, scene) = await Service.Framework.Run(() =>
				{
					var scene = new SceneDefinition();
					scene.FillFromActiveLayout();
					var cacheKey = GetCacheKey(scene);
					return (cacheKey, scene);
				}, cancel);

				Log($"Kicking off build for '{cacheKey}' (reload={allowLoadFromCache})");
				var navmesh = await Task.Run(() => BuildNavmesh(scene, cacheKey, allowLoadFromCache, cancel), cancel);
				Log($"Mesh loaded: '{cacheKey}'");
				Navmesh = navmesh;
				Query = new(Navmesh);

				var ff = await FloodFill.GetAsync();
				if (ff.TryLookup(scene.TerritoryID, out var points))
					Prune(points);

				OnNavmeshChanged?.Invoke(Navmesh, Query);
			}, cts.Token);
		}
		return true;
	}

	internal void ReplaceMesh(Navmesh mesh)
	{
		Navmesh = mesh;
		Query = new(Navmesh);
		Log($"Mesh replaced");
		OnNavmeshChanged?.Invoke(Navmesh, Query);
	}

	private static bool InCutscene => Service.Condition[ConditionFlag.WatchingCutscene] || Service.Condition[ConditionFlag.OccupiedInCutSceneEvent];

	public Task<List<Waypoint>> QueryPath(Vector3 from, Vector3 to, bool flying, float range = 0, CancellationToken externalCancel = default, Vector3? avoidCenter = null, float avoidRadius = 0)
	{
		if (_currentCTS == null)
			throw new Exception($"Can't initiate query - navmesh is not loaded");

		// task can be cancelled either by internal request (i.e. when navmesh is reloaded) or external
		var combined = CancellationTokenSource.CreateLinkedTokenSource(_currentCTS.Token, externalCancel);
		++_numActivePathfinds;
		return ExecuteWhenIdle(async cancel =>
		{
			using var autoDisposeCombined = combined;
			using var autoDecrementCounter = new OnDispose(() => --_numActivePathfinds);
			LogInfo($"Kicking off pathfind from {from} to {to}");
			var path = await Task.Run(() =>
			{
				combined.Token.ThrowIfCancellationRequested();
				if (Query == null)
					throw new Exception($"Can't pathfind, navmesh did not build successfully");
				Log($"Executing pathfind from {from} to {to}");
				if (flying)
					return Query.PathfindVolume(from, to, UseRaycasts, UseStringPulling, combined.Token, avoidCenter, avoidRadius);
				// same fast-path as volume: don't pay for avoid filtering when the straight line never enters
				IDtQueryFilter? filter = avoidRadius > 0 && avoidCenter is { } center && NavmeshQuery.SegmentEntersAvoid(from, to, center, avoidRadius)
					? new NavmeshQuery.AvoidRadiusFilter(center, avoidRadius) : null;
				return Query.PathfindMesh(from, to, UseRaycasts, UseStringPulling, range, combined.Token, filter);
			}, combined.Token);
			Log($"Pathfinding done: {path.Count} waypoints");
			return path;
		}, combined.Token);
	}

	public async Task<List<Vector3>> QueryPathBasic(Vector3 from, Vector3 to, bool flying, float range = 0, CancellationToken externalCancel = default, Vector3? avoidCenter = null, float avoidRadius = 0)
	{
		var result = await QueryPath(from, to, flying, range, externalCancel, avoidCenter, avoidRadius);
		return [.. result.Select(w => w.Position)];
	}

	// note: pixelSize should be power-of-2
	// 地形を .bmp に書き出す BuildBitmap は、取り込みにあたって外した。
	// 中身を目で見るための開発用の機能で、周回には使わない。
	// どこからも呼ばれていない。

	// if non-empty string is returned, active layout is ready
	private unsafe string GetCurrentKey()
	{
		var layout = LayoutWorld.Instance()->ActiveLayout;
		if (layout == null || layout->InitState != 7 || layout->FestivalStatus is > 0 and < 5)
			return ""; // layout not ready

		var filter = LayoutUtils.FindFilter(layout);
		var filterKey = filter != null ? filter->Key : 0;

		var terrRow = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId);

		// CE always has a festival layer (i hope). the non-festival layout is briefly loaded when entering the zone, which triggers a useless mesh build (which is also expensive because the zone is large)
		if (terrRow?.TerritoryIntendedUse.RowId == 60)
		{
			var fest = layout->ActiveFestivals[0];
			if (fest.Id == 0 && fest.Phase == 0)
				return "";
		}

		var sgs = LayoutUtils.GetZoneSharedGroupsEnabled(filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId);

		return $"{terrRow?.Bg}//{filterKey:X}//{LayoutUtils.FestivalsString(layout->ActiveFestivals)}//{string.Join('.', sgs)}";
	}

	internal static unsafe string GetCacheKey(SceneDefinition scene)
	{
		// note: festivals are active globally, but majority of zones don't have festival-specific layers, so we only want real ones in the cache key
		var layout = LayoutWorld.Instance()->ActiveLayout;
		var filter = LayoutUtils.FindFilter(layout);
		var filterKey = filter != null ? filter->Key : 0;
		var terrId = filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId;
		var terrRow = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(terrId);

		static string numbers<T>(IEnumerable<T> nums) where T : INumber<T> => string.Join('.', nums.Select(n => n.ToString("X", CultureInfo.InvariantCulture)));

		return $"{terrRow?.Bg.ToString().Replace('/', '_')}__{filterKey:X}__{numbers(scene.FestivalLayers)}__{numbers(scene.ZoneSGs)}";
	}

	private void ClearState()
	{
		if (_currentCTS == null)
			return; // already cleared

		var cts = _currentCTS;
		_currentCTS = null;
		cts.Cancel();
		Log("Queueing state clear");
		ExecuteWhenIdle(() =>
		{
			Log("Clearing state");
			_numActivePathfinds = 0;
			cts.Dispose();
			OnNavmeshChanged?.Invoke(null, null);
			Query = null;
			Navmesh = null;
		}, default);
	}

	private Navmesh BuildNavmesh(SceneDefinition scene, string cacheKey, bool allowLoadFromCache, CancellationToken cancel)
	{
		Log($"Build task started: '{cacheKey}'");
		var customization = NavmeshCustomizationRegistry.ForTerritory(scene.TerritoryID);
		Log($"Customization for '{scene.TerritoryID}': {customization.GetType()}");

		var layers = scene.FestivalLayers.ToList();

		// try reading from cache
		var cache = new FileInfo($"{_cacheDir.FullName}/{cacheKey}.navmesh");
		if (allowLoadFromCache && cache.Exists)
		{
			try
			{
				Log($"Loading cache: {cache.FullName}");
				using var stream = cache.OpenRead();
				using var reader = new BinaryReader(stream);
				var mesh = Navmesh.Deserialize(reader, customization.Version);
				customization.CustomizeMesh(mesh, layers);
				return mesh;
			}
			catch (Exception ex)
			{
				Log($"Failed to load cache: {ex}");
			}
		}
		cancel.ThrowIfCancellationRequested();

		// cache doesn't exist or can't be used for whatever reason - build navmesh from scratch
		var builder = new NavmeshBuilder(scene, customization);
		var deltaProgress = 0.99f / (builder.NumTilesX * builder.NumTilesZ);
		builder.BuildTiles(() =>
		{
			_loadTaskProgress += deltaProgress;
			cancel.ThrowIfCancellationRequested();
		});

		// write results to cache
		//
		// AutoTreasure change: several game clients run this plugin at the same
		// time and share one cache folder. When two of them build the same zone,
		// both try to create the same file and one dies with a sharing violation,
		// losing the whole build.
		//
		// Write to a private temp file first, then move it into place. A move is
		// atomic, so readers never see a half-written file. If another client won
		// the race and the file already exists, its copy is just as good - keep it
		// and drop ours.
		try
		{
			var temp = new FileInfo($"{cache.FullName}.{Environment.ProcessId}.tmp");

			Service.Log.Debug($"Writing cache: {cache.FullName}");

			using (var stream = temp.Open(FileMode.Create, FileAccess.Write, FileShare.None))
			using (var writer = new BinaryWriter(stream))
			{
				builder.Navmesh.Serialize(writer);
			}

			try
			{
				temp.MoveTo(cache.FullName, overwrite: false);
			}
			catch (IOException)
			{
				// Someone else finished first. Their file is equivalent, so use it.
				Service.Log.Debug($"Cache already written by another client: {cache.FullName}");
				try { temp.Delete(); } catch { }
			}
		}
		catch (Exception ex)
		{
			// Failing to cache is not fatal - the mesh is already built in memory.
			// Losing the build over a file error would be much worse.
			Service.Log.Warning($"Failed to write cache: {ex.Message}");
		}
		customization.CustomizeMesh(builder.Navmesh, layers);
		deltaProgress += 0.01f;
		return builder.Navmesh;
	}

	// A load that never finishes leaves the mesh null forever, and the only way out
	// used to be zoning again by hand. Notice it and retry once.
	private void WatchForStuckLoad()
	{
		// Loaded, or nothing was ever asked for - nothing to watch.
		if (Navmesh != null || CurrentKey.Length == 0)
		{
			_loadStuckSince = null;
			_stuckRetries = 0;
			return;
		}

		// A build in progress reports progress; that is healthy, just slow.
		if (_loadTaskProgress >= 0)
		{
			_loadStuckSince = DateTime.Now;
			return;
		}

		_loadStuckSince ??= DateTime.Now;

		if ((DateTime.Now - _loadStuckSince.Value).TotalSeconds < StuckLoadSeconds)
			return;

		if (_stuckRetries >= MaxStuckRetries)
			return;

		++_stuckRetries;
		_loadStuckSince = null;
		Service.Log.Warning($"[NavmeshManager] Load appears stuck; retrying ({_stuckRetries}/{MaxStuckRetries})");
		Reload(true);
	}

	// If the head of the task chain can no longer complete, replace it with a fresh
	// completed task. Without this, one cancelled-before-start task blocks every
	// subsequent load and pathfind for the rest of the session.
	// The previous task to wait on. A cancelled or faulted task still completes, so
	// awaiting it is safe - but a task that was cancelled *before starting* never
	// transitions at all, which would stall the whole chain. Treat anything that is
	// already finished-but-unusable as "nothing to wait for".
	private Task PrevOrCompleted()
	{
		var prev = _lastLoadQueryTask;
		return prev.IsCanceled || prev.IsFaulted ? Task.CompletedTask : prev;
	}

	private void ResetTaskChainIfStuck()
	{
		var prev = _lastLoadQueryTask;
		if (prev.IsCanceled || prev.IsFaulted)
		{
			Log($"Task chain was stuck ({prev.Status}); resetting");
			_lastLoadQueryTask = Task.CompletedTask;
		}
	}

	private void ExecuteWhenIdle(Action task, CancellationToken token)
	{
		var prev = PrevOrCompleted();
		_lastLoadQueryTask = Service.Framework.Run(async () =>
		{
			await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
			_ = prev.Exception;
			task();
		}, token);
	}

	private void ExecuteWhenIdle(Func<CancellationToken, Task> task, CancellationToken token)
	{
		var prev = PrevOrCompleted();
		_lastLoadQueryTask = Service.Framework.Run(async () =>
		{
			await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
			_ = prev.Exception;
			var t = task(token);
			await t.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
			LogTaskError(t);
		}, token);
	}

	private Task<T> ExecuteWhenIdle<T>(Func<CancellationToken, Task<T>> task, CancellationToken token)
	{
		var prev = PrevOrCompleted();
		var res = Service.Framework.Run(async () =>
		{
			await prev.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
			_ = prev.Exception;
			var t = task(token);
			await ((Task)t).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
			LogTaskError(t);
			return t.Result;
		}, token);
		_lastLoadQueryTask = res;
		return res;
	}

	private static void Log(string message) => Service.Log.Debug($"[NavmeshManager] [{Environment.CurrentManagedThreadId}] {message}");
	private static void LogInfo(string message) => Service.Log.Info($"[NavmeshManager] [{Environment.CurrentManagedThreadId}] {message}");
	private static void LogTaskError(Task task)
	{
		if (task.IsFaulted)
			Service.Log.Error($"[NavmeshManager] Task failed with error: {task.Exception}");
	}

	public void Prune(IEnumerable<Vector3> points)
	{
		if (Navmesh == null || Query == null)
			throw new InvalidOperationException("can't prune, mesh is missing");

		var startPolys = points.Select(pt => Query.FindNearestMeshPoly(pt));
		Log($"seeding from start polys: {string.Join(", ", startPolys.Select(p => p.ToString("X")))}");
		var reachablePolys = Query.FindReachableMeshPolys([.. startPolys]);

		var pruneCount = 0;
		for (var i = 0; i < Navmesh.Mesh.GetMaxTiles(); i++)
		{
			var t = Navmesh.Mesh.GetTile(i);
			if (t.data?.header == null)
				continue;

			var prBase = Navmesh.Mesh.GetPolyRefBase(t);
			for (var j = 0; j < t.data.header.polyCount; j++)
			{
				var pref = prBase | (uint)j;
				if (Navmesh.Mesh.GetPolyFlags(pref, out var fl).Failed())
				{
					Log($"failed to fetch flags for {pref:X}");
					continue;
				}
				if (reachablePolys.Contains(pref))
				{
					if (Navmesh.Mesh.SetPolyFlags(pref, fl & ~Navmesh.FLAG_UNREACHABLE).Failed())
						Log($"failed to set flags for {pref:X}");
				}
				else
				{
					pruneCount++;
					if (Navmesh.Mesh.SetPolyFlags(pref, fl | Navmesh.FLAG_UNREACHABLE).Failed())
						Log($"failed to set flags for {pref:X}");
				}
			}
		}

		Log($"pruned {pruneCount} unreachable polygons");
	}
}
