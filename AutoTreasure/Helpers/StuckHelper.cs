using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoTreasure.Helpers;

/// <summary>
/// 進まなくなったことに気づく。
///
/// 自動化で一番困るのは「エラーも出ないまま、ずっと同じ場所にいる」状態。
/// 壁に引っかかった、目的地にたどり着けない、経路が引けていない——
/// どれも見た目は「動いていない」だけで、放っておくといつまでも終わらない。
///
/// 2つの見方で気づく。
///   1. ほとんど動いていない
///   2. 動いてはいるが、行ったり来たりで前に進んでいない
///
/// 2つ目が要る理由は、壁に沿って往復すると「移動距離」は稼げてしまうため。
/// 出発点からの直線距離で見ることで、これを見抜く。
/// </summary>
internal sealed class StuckDetector
{
    private readonly List<(DateTime Time, Vector3 Position)> _samples = [];

    private DateTime _lastSample = DateTime.MinValue;
    private Vector3 _anchor;
    private DateTime _anchorTime = DateTime.MinValue;

    /// <summary>位置を記録する間隔。</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>この距離のうちに留まっていたら「動いていない」とみなす。</summary>
    private const float StuckRadius = 3f;

    /// <summary>どれだけ動かなければ詰まりとみなすか。</summary>
    private readonly TimeSpan _stuckThreshold;

    /// <summary>往復を見抜くために遡る時間。</summary>
    private static readonly TimeSpan LoopWindow = TimeSpan.FromSeconds(8);

    /// <summary>この距離以上を動いていながら…</summary>
    private const float LoopTotalDistance = 12f;

    /// <summary>…出発点からこれしか離れていなければ、往復しているとみなす。</summary>
    private const float LoopNetDistance = 5f;

    public StuckDetector(double stuckSeconds = 10.0)
    {
        _stuckThreshold = TimeSpan.FromSeconds(stuckSeconds);
    }

    /// <summary>
    /// 見張りを最初からやり直す。
    /// 目的地を変えたとき、エリアを移ったときなど、状況が変わったら必ず呼ぶ。
    /// </summary>
    public void Reset()
    {
        _samples.Clear();
        _lastSample = DateTime.MinValue;
        _anchorTime = DateTime.MinValue;
    }

    /// <summary>
    /// 毎フレーム呼ぶ。詰まっていれば true を返す。
    ///
    /// true を返したあとは自動で初期化されるので、
    /// 呼び出し側は「詰まったときの対処」を1回だけ行えばよい。
    /// </summary>
    public bool Check()
    {
        if (!PlayerHelper.IsValid)
        {
            Reset();
            return false;
        }

        var now = DateTime.UtcNow;
        if (now - _lastSample < SampleInterval)
            return false;

        _lastSample = now;
        var position = PlayerHelper.Position;

        // 記録をためる。古いものは捨てる。
        _samples.Add((now, position));
        _samples.RemoveAll(s => now - s.Time > LoopWindow);

        // 1つ目の見方: ほとんど動いていない
        if (_anchorTime == DateTime.MinValue || Vector3.Distance(position, _anchor) > StuckRadius)
        {
            _anchor = position;
            _anchorTime = now;
        }
        else if (now - _anchorTime >= _stuckThreshold)
        {
            Reset();
            return true;
        }

        // 2つ目の見方: 動いてはいるが前に進んでいない
        if (_samples.Count >= 4 && now - _samples[0].Time >= LoopWindow * 0.8)
        {
            var travelled = 0f;
            for (var i = 1; i < _samples.Count; i++)
                travelled += Vector3.Distance(_samples[i - 1].Position, _samples[i].Position);

            var net = Vector3.Distance(_samples[0].Position, position);

            if (travelled >= LoopTotalDistance && net <= LoopNetDistance)
            {
                Reset();
                return true;
            }
        }

        return false;
    }
}
