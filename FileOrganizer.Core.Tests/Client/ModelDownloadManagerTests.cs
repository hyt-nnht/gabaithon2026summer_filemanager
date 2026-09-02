using FileOrganizer.Core.Client;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Client;

/// <summary>
/// <see cref="ModelDownloadManager"/> の単体テスト。
/// 仕様書§3.1「デモ用事前配置モデルフラグ」の判定ロジック（<see cref="ModelDownloadManager.CheckPreloadedModelAsync"/>）
/// を実ファイルI/O（一時フォルダ）で検証する。<see cref="ModelDownloadManager.DownloadModelAsync"/>は
/// Phase3まで未実装（<see cref="NotImplementedException"/>）であることのみ確認する。
/// </summary>
public class ModelDownloadManagerTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "ModelDownloadManager", Guid.NewGuid().ToString("N"));
    private readonly ModelDownloadManager _manager = new();

    public ModelDownloadManagerTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch
        {
            // 一時フォルダの後始末失敗は無視。
        }
    }

    private string CreateModelFile(long sizeBytes)
    {
        string path = Path.Combine(_workDir, "model.gguf");
        using (var fs = new FileStream(path, FileMode.Create))
        {
            fs.SetLength(sizeBytes);
        }
        return path;
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_UsePreloadedSlmModelがfalseならDisabledを返す()
    {
        var settings = new AppSettings { UsePreloadedSlmModel = false, SlmModelPath = CreateModelFile(ModelDownloadManager.ExpectedModelSizeBytes) };

        var result = await _manager.CheckPreloadedModelAsync(settings);

        Assert.Equal(ModelAvailabilityStatus.Disabled, result.Status);
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_SlmModelPathが未設定ならPathNotConfiguredを返す()
    {
        var settings = new AppSettings { UsePreloadedSlmModel = true, SlmModelPath = "" };

        var result = await _manager.CheckPreloadedModelAsync(settings);

        Assert.Equal(ModelAvailabilityStatus.PathNotConfigured, result.Status);
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_指定パスにファイルが無ければFileNotFoundを返す()
    {
        var settings = new AppSettings
        {
            UsePreloadedSlmModel = true,
            SlmModelPath = Path.Combine(_workDir, "missing-model.gguf"),
        };

        var result = await _manager.CheckPreloadedModelAsync(settings);

        Assert.Equal(ModelAvailabilityStatus.FileNotFound, result.Status);
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_期待サイズの半分未満ならSizeTooSmallを返す()
    {
        long tooSmallSize = (long)(ModelDownloadManager.ExpectedModelSizeBytes * 0.1);
        var settings = new AppSettings
        {
            UsePreloadedSlmModel = true,
            SlmModelPath = CreateModelFile(tooSmallSize),
        };

        var result = await _manager.CheckPreloadedModelAsync(settings);

        Assert.Equal(ModelAvailabilityStatus.SizeTooSmall, result.Status);
        Assert.False(result.IsReady);
        Assert.Equal(tooSmallSize, result.ActualSizeBytes);
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_期待サイズ相当のファイルが存在すればReadyを返す()
    {
        var settings = new AppSettings
        {
            UsePreloadedSlmModel = true,
            SlmModelPath = CreateModelFile(ModelDownloadManager.ExpectedModelSizeBytes),
        };

        var result = await _manager.CheckPreloadedModelAsync(settings);

        Assert.Equal(ModelAvailabilityStatus.Ready, result.Status);
        Assert.True(result.IsReady);
        Assert.Equal(settings.SlmModelPath, result.ModelPath);
        Assert.Equal(ModelDownloadManager.ExpectedModelSizeBytes, result.ActualSizeBytes);
    }

    [Fact]
    public async Task CheckPreloadedModelAsync_settingsがnullの場合は例外を投げる()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _manager.CheckPreloadedModelAsync(null!));
    }

    [Fact]
    public async Task DownloadModelAsync_Phase3で未実装のためNotImplementedExceptionを投げる()
    {
        string destinationPath = Path.Combine(_workDir, "downloaded-model.gguf");
        var progressValues = new List<double>();
        var progress = new Progress<double>(v => progressValues.Add(v));

        await Assert.ThrowsAsync<NotImplementedException>(
            () => _manager.DownloadModelAsync(destinationPath, progress));
    }

    [Fact]
    public async Task DownloadModelAsync_destinationPathが空の場合は例外を投げる()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.DownloadModelAsync(""));
    }
}
