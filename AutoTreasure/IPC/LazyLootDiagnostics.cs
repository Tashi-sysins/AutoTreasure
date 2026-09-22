using Dalamud.Game.Chat;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;

namespace AutoTreasure.IPC;

/// <summary>ロット開始通知だけを観測する。チャットや LazyLoot の設定・処理は変更しない。</summary>
internal sealed class LazyLootDiagnostics : IDisposable
{
    private readonly Action<string> report;
    private string? expected;

    internal LazyLootDiagnostics(Action<string> report)
    {
        this.report = report;
        Svc.Chat.ChatMessageHandled += OnMessage;
        Svc.Chat.ChatMessageUnhandled += OnMessage;
    }

    private void OnMessage(IChatMessage message)
    {
        try
        {
            expected ??= Svc.Data.GetExcelSheet<LogMessage>().GetRow(5194).Text.ExtractText();
            var original = message.OriginalMessage.ExtractText();
            if (original != expected && message.Message.TextValue != expected) return;
            report($"LazyLoot 通知診断: 種類={message.LogKind}, 非表示={message.IsHandled}, "
                + $"原文一致={original == expected}, "
                + $"変更後一致={message.Message.TextValue == expected}; {LazyLootControl.Diagnostic()}");
        }
        catch (Exception ex)
        {
            Svc.Log.Debug(ex, "[AutoTreasure] ロット通知の診断を取得できませんでした。");
        }
    }

    public void Dispose()
    {
        Svc.Chat.ChatMessageHandled -= OnMessage;
        Svc.Chat.ChatMessageUnhandled -= OnMessage;
    }
}
