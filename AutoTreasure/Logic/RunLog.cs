using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace AutoTreasure.Logic;

/// <summary>
/// 何が起きたかをファイルに残す。
///
/// 自動化を作り込むには、実際に手で1周したときの様子が要る。
///   ・宝箱や扉が、どういう種類・IDのオブジェクトとして現れるか
///   ・ディグを使ったとき、どのアクションIDが飛ぶか
///   ・魔紋の中で、部屋が変わるたびに何が変化するか
///   ・脱出ポータルが、他の仕掛けとどう違うか
///
/// こうしたことは実機でしか分からない。手で遊んでいる間に記録しておけば、
/// あとから落ち着いて読める。
///
/// ゲームのログ（/xllog）は流れて消えるうえ、件数にも限りがある。
/// ファイルに書けば全部残る。
/// </summary>
internal sealed class RunLog : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _path = "";

    // 機械で読む用の控え。人が読む方とは別に、1行1件のJSONでも残す。
    // あとから差分を取ったり、条件を洗い出したりするのに使う。
    private StreamWriter? _jsonWriter;
    private string _jsonPath = "";

    /// <summary>今このログを書いているか。</summary>
    internal bool IsRecording { get; private set; }

    /// <summary>書き出し先（人が読む方）。</summary>
    internal string Path => _path;

    /// <summary>書き出し先（機械で読む方）。</summary>
    internal string JsonPath => _jsonPath;

    /// <summary>書いた行数。</summary>
    internal int LineCount { get; private set; }

    /// <summary>ログを置くフォルダ。</summary>
    internal static string LogDirectory => @"C:\自作プラグイン\AutoTreasure\logs";

    /// <summary>
    /// 今動かしているキャラクターの名前。
    /// 読めないときは「不明」。ログイン前など。
    /// </summary>
    internal static string CharacterName
    {
        get
        {
            try
            {
                var name = Svc.Objects.LocalPlayer?.Name.TextValue;
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
            catch
            {
                // 読めない場面は名無しで進める。
            }

            return "不明";
        }
    }

    /// <summary>
    /// 記録を始める。
    /// すでに書いている場合は、いったん閉じて新しいファイルにする。
    /// </summary>
    internal void Start(string label = "")
    {
        Stop();

        try
        {
            Directory.CreateDirectory(LogDirectory);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var suffix = string.IsNullOrWhiteSpace(label) ? "" : "_" + Sanitize(label);

            // 誰の記録かをファイル名に入れる。
            //
            // 同じパソコンで複数のキャラクターを動かすため、これが無いと
            // どれが誰の記録か分からなくなる。さらに悪いことに、
            // 同じ秒に始めると同じファイルへ書き込んで中身が混ざる。
            var who = Sanitize(CharacterName);
            _path = System.IO.Path.Combine(LogDirectory, $"{who}_{stamp}{suffix}.log");

            // 追記で開く。ゲームが落ちても、そこまでの内容は残る。
            _writer = new StreamWriter(_path, append: true, new UTF8Encoding(true))
            {
                AutoFlush = true,   // すぐ書き出す。落ちたときに失わないため
            };

            _jsonPath = System.IO.Path.Combine(LogDirectory, $"{who}_{stamp}{suffix}.jsonl");
            _jsonWriter = new StreamWriter(_jsonPath, append: true, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            IsRecording = true;
            LineCount = 0;

            WriteHeader();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "記録を始められませんでした。");
            IsRecording = false;
        }
    }

    /// <summary>記録を終える。</summary>
    internal void Stop()
    {
        lock (_lock)
        {
            if (_writer != null)
            {
                try
                {
                    _writer.WriteLine();
                    _writer.WriteLine($"=== 記録終了 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch
                {
                    // 閉じるときの失敗は流す。
                }
                _writer = null;
            }

            if (_jsonWriter != null)
            {
                try { _jsonWriter.Flush(); _jsonWriter.Dispose(); } catch { }
                _jsonWriter = null;
            }

            IsRecording = false;
        }
    }

    /// <summary>1行書く。</summary>
    internal void Write(string text)
    {
        if (!IsRecording)
            return;

        lock (_lock)
        {
            if (_writer == null)
                return;

            try
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {text}");
                LineCount++;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "記録に失敗しました。");
            }
        }
    }

    /// <summary>
    /// 機械で読む用に、1行のJSONを書く。
    ///
    /// 人が読む方は流れを追うため、こちらは後から条件を洗い出すため。
    /// 目的が違うので、両方残す。
    /// </summary>
    internal void WriteJson(string json)
    {
        if (!IsRecording)
            return;

        lock (_lock)
        {
            if (_jsonWriter == null)
                return;

            try
            {
                _jsonWriter.WriteLine(json);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "JSONの記録に失敗しました。");
            }
        }
    }

    /// <summary>JSONの文字列として安全な形にする。</summary>
    internal static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (char.IsControl(c))
                        sb.Append(' ');
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>区切りの見出しを書く。あとから読むときの目印。</summary>
    internal void WriteSection(string title)
    {
        if (!IsRecording)
            return;

        Write("");
        Write("──────────────────────────────────────────");
        Write($"  {title}");
        Write("──────────────────────────────────────────");
    }

    /// <summary>座標を読みやすい形にする。</summary>
    internal static string Format(Vector3 v)
        => $"({v.X,8:F2}, {v.Y,7:F2}, {v.Z,8:F2})";

    private void WriteHeader()
    {
        Write("===========================================");
        Write("  AutoTreasure 動作記録");
        Write($"  キャラクター: {CharacterName}");
        Write($"  役割        : {RoleText}");
        Write($"  開始        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Write("===========================================");
        Write("");
        Write("この記録は、手で遊んでいる間に何が起きたかを残したもの。");
        Write("自動化を組むための材料にする。");
        Write("");
    }

    /// <summary>このクライアントの役割を、読める形で返す。</summary>
    private static string RoleText => Plugin.Config.Role switch
    {
        ClientRole.Leader => "リーダー",
        ClientRole.Member => "メンバー",
        _                 => "単独",
    };

    /// <summary>ファイル名に使えない文字を取り除く。</summary>
    private static string Sanitize(string text)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    /// <summary>これまでに書いたログの一覧を、新しい順に返す。</summary>
    internal static List<string> ListLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
                return [];

            var files = new List<string>(Directory.GetFiles(LogDirectory, "*.log"));
            files.Sort((a, b) => string.CompareOrdinal(b, a));   // 新しい順
            return files;
        }
        catch
        {
            return [];
        }
    }

    public void Dispose() => Stop();
}
