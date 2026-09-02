namespace FileOrganizer.Widgets.QuickLook;

/// <summary>
/// WH_KEYBOARD_LL側で取得した最小限の状態から、Quick Lookを開いてよいかを純粋判定する。
/// このクラス自身はキーフックやWin32 APIを呼ばないため、UI開発段階でも安全に検証できる。
/// </summary>
public static class QuickLookActivationPolicy
{
    public static QuickLookActivationDecision Evaluate(QuickLookActivationContext context)
    {
        // 順序が重要。最頻の不一致を最初に返し、キーフック内の処理を最小化する。
        if (!context.IsSpaceKeyDown)
            return QuickLookActivationDecision.Suppress(QuickLookSuppressionReason.NotSpaceKey);
        if (context.IsImeComposing)
            return QuickLookActivationDecision.Suppress(QuickLookSuppressionReason.ImeComposition);
        if (!context.IsExplorerForeground)
            return QuickLookActivationDecision.Suppress(QuickLookSuppressionReason.NotExplorer);
        if (context.IsEditControlFocused)
            return QuickLookActivationDecision.Suppress(QuickLookSuppressionReason.TextEditing);
        if (!context.IsFileListFocused)
            return QuickLookActivationDecision.Suppress(QuickLookSuppressionReason.FileListNotFocused);
        if (context.IsFullScreenApplicationActive)
            return QuickLookActivationDecision.Suppress(QuickLookSuppressionReason.FullScreenApplication);
        if (string.IsNullOrWhiteSpace(context.SelectedFilePath))
            return QuickLookActivationDecision.Suppress(QuickLookSuppressionReason.NoSelectedFile);

        return new QuickLookActivationDecision(true, QuickLookSuppressionReason.None, context.SelectedFilePath);
    }
}

public sealed record QuickLookActivationContext(
    bool IsSpaceKeyDown,
    bool IsImeComposing,
    bool IsExplorerForeground,
    bool IsEditControlFocused,
    bool IsFileListFocused,
    bool IsFullScreenApplicationActive,
    string? SelectedFilePath);

public sealed record QuickLookActivationDecision(
    bool ShouldOpen,
    QuickLookSuppressionReason SuppressionReason,
    string? SelectedFilePath)
{
    public static QuickLookActivationDecision Suppress(QuickLookSuppressionReason reason)
        => new(false, reason, null);
}

public enum QuickLookSuppressionReason
{
    None,
    NotSpaceKey,
    ImeComposition,
    NotExplorer,
    TextEditing,
    FileListNotFocused,
    FullScreenApplication,
    NoSelectedFile
}
