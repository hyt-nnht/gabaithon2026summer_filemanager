using System.IO;

namespace FileOrganizer.Core.Services;

/// <summary>
/// <see cref="StartupRecoveryService"/>がファイル実在確認に使う最小限のファイルシステム抽象化。
/// 単体テストで<c>File.Exists</c>相当の結果をモック化できるようにするために切り出している
/// （実ファイルI/Oを伴わずにExecuting/Undoing復旧ロジックの分岐を検証するため）。
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);
}

/// <summary>
/// <see cref="System.IO.File.Exists"/>ベースの既定実装。本番実行時はこちらを使用する。
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
}
