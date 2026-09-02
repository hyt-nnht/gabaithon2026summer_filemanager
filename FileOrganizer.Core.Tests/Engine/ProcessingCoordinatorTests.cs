using FileOrganizer.Core.Database;
using FileOrganizer.Core.Engine;
using FileOrganizer.Core.Services;
using FileOrganizer.Core.Watcher;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;

namespace FileOrganizer.Core.Tests.Engine;

/// <summary>設定・ルールをメモリ上に保持するだけの<see cref="ISettingsRepository"/>フェイク実装。</summary>
internal sealed class FakeSettingsRepository : ISettingsRepository
{
    public AppSettings Settings { get; set; } = new();
    public List<RuleModel> Rules { get; set; } = new();

    public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Settings);

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }

    public Task<List<RuleModel>> LoadRulesAsync(CancellationToken ct = default) => Task.FromResult(Rules);

    public Task SaveRulesAsync(List<RuleModel> rules, CancellationToken ct = default)
    {
        Rules = rules;
        return Task.CompletedTask;
    }

    public Task RestorePresetRulesAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// テスト用<see cref="IOcrService"/>フェイク。呼び出し回数・引数を記録し、戻り値/例外を差し替えられる。
/// </summary>
internal sealed class FakeOcrService : IOcrService
{
    public bool LanguagePackAvailable { get; set; } = true;
    public string? OcrTextToReturn { get; set; }
    public bool ThrowOnExtract { get; set; }
    public int ExtractTextCallCount { get; private set; }
    public string? LastRequestedFilePath { get; private set; }

    public Task<bool> IsLanguagePackAvailableAsync() => Task.FromResult(LanguagePackAvailable);

    public Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        ExtractTextCallCount++;
        LastRequestedFilePath = filePath;
        if (ThrowOnExtract)
        {
            throw new InvalidOperationException("OCR抽出失敗（テスト用シミュレーション）");
        }
        return Task.FromResult(OcrTextToReturn);
    }
}

/// <summary>
/// テスト用<see cref="IPythonApiClient"/>フェイク。呼び出し回数・最後のリクエストを記録し、
/// 戻り値/例外を差し替えられる。
/// </summary>
internal sealed class FakePythonApiClient : IPythonApiClient
{
    public AnalyzeResponse? ResponseToReturn { get; set; }
    public bool ThrowOnAnalyze { get; set; }
    public int AnalyzeCallCount { get; private set; }
    public AnalyzeRequest? LastRequest { get; private set; }

    public void Configure(int port, string bearerToken) { }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<AnalyzeResponse?> AnalyzeAsync(AnalyzeRequest request, CancellationToken ct = default)
    {
        AnalyzeCallCount++;
        LastRequest = request;
        if (ThrowOnAnalyze)
        {
            throw new InvalidOperationException("Python API呼び出し失敗（テスト用シミュレーション）");
        }
        return Task.FromResult(ResponseToReturn);
    }
}

/// <summary>
/// 実<see cref="IHistoryRepository"/>実装をラップし、Insert/UpdateStateの呼び出し順序（状態遷移の
/// 実際の順番）を記録する。DBへの実書き込みは<paramref name="inner"/>にそのまま委譲する。
/// </summary>
internal sealed class RecordingHistoryRepository : IHistoryRepository
{
    private readonly IHistoryRepository _inner;
    public List<string> Events { get; } = new();

    public RecordingHistoryRepository(IHistoryRepository inner) => _inner = inner;

    public async Task<long> InsertAsync(HistoryRecord record, CancellationToken ct = default)
    {
        long id = await _inner.InsertAsync(record, ct);
        Events.Add($"Insert:{record.State}");
        return id;
    }

    public async Task UpdateStateAsync(long id, OperationState newState, string? errorMessage = null, CancellationToken ct = default)
    {
        await _inner.UpdateStateAsync(id, newState, errorMessage, ct);
        Events.Add($"Update:{newState}");
    }

    public Task<HistoryRecord?> GetByIdAsync(long id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);

    public Task<HistoryRecord?> GetByOperationIdAsync(string operationId, CancellationToken ct = default) => _inner.GetByOperationIdAsync(operationId, ct);

    public Task<IReadOnlyList<HistoryRecord>> GetRecordsByStateAsync(OperationState state, CancellationToken ct = default) => _inner.GetRecordsByStateAsync(state, ct);

    public Task<IReadOnlyList<HistoryRecord>> GetRecentAsync(int count, CancellationToken ct = default) => _inner.GetRecentAsync(count, ct);
}

/// <summary>
/// 仕様書§3.3「実行時フロー」（Planned→Executing→Completed/Failed）が実際のファイル操作と
/// 連動して動作することを検証する。対象: <see cref="ProcessingCoordinator"/>。
/// 1-3 <see cref="SqliteHistoryRepository"/>（一時DB）・1-7 <see cref="RuleEvaluator"/>・
/// 1-8 <see cref="FileOperationService"/>はいずれも実実装を使用し、実際のファイルI/Oを一時フォルダで行う。
/// </summary>
public class ProcessingCoordinatorTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FileOrganizerTests", "ProcessingCoordinator", Guid.NewGuid().ToString("N"));
    private readonly string _sourceDir;
    private readonly string _destDir;
    private readonly IHistoryRepository _realRepository;

    public ProcessingCoordinatorTests()
    {
        _sourceDir = Path.Combine(_workDir, "source");
        _destDir = Path.Combine(_workDir, "dest");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_destDir);

        string connectionString = DatabaseInitializer.BuildConnectionString(Path.Combine(_workDir, "history.db"));
        new DatabaseInitializer(connectionString).InitializeAsync().GetAwaiter().GetResult();
        _realRepository = new SqliteHistoryRepository(connectionString);
    }

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

    private string CreateSourceFile(string fileName, string content = "content")
    {
        string path = Path.Combine(_sourceDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static FileMetadata BuildMetadata(string path)
    {
        var info = new FileInfo(path);
        return new FileMetadata
        {
            FullPath = path,
            FileName = info.Name,
            Extension = info.Extension,
            SizeBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            CreatedTimeUtc = info.CreationTimeUtc,
        };
    }

    private static RuleCondition Cond(string type, string op, object? value) => new() { Type = type, Operator = op, Value = value };

    private static RuleAction MoveTo(string destination) => new() { Type = "move", Destination = destination };
    private static RuleAction CopyTo(string destination) => new() { Type = "copy", Destination = destination };
    private static RuleAction RenameTo(string pattern) => new() { Type = "rename", Pattern = pattern };
    private static RuleAction Recycle() => new() { Type = "recycle" };

    private static RuleModel CreateRule(string name, RuleCondition condition, params RuleAction[] actions) => new()
    {
        Name = name,
        Enabled = true,
        Conditions = new List<RuleCondition> { condition },
        Actions = actions.ToList(),
    };

    private ProcessingCoordinator CreateCoordinator(
        FakeSettingsRepository settingsRepository,
        IHistoryRepository? historyRepository = null,
        ConflictPolicy defaultConflictPolicy = ConflictPolicy.AutoRename,
        IOcrService? ocrService = null,
        IPythonApiClient? pythonApiClient = null)
    {
        return new ProcessingCoordinator(
            ruleEngine: new RuleEvaluator(),
            historyRepository: historyRepository ?? _realRepository,
            fileOperationService: new FileOperationService(watchSuppressor: null, renameConflictPolicy: defaultConflictPolicy),
            settingsRepository: settingsRepository,
            ocrService: ocrService,
            pythonApiClient: pythonApiClient,
            defaultConflictPolicy: defaultConflictPolicy);
    }

    // --- 基本パイプライン: Planned→Executing→Completed（実ファイル操作連動） -------------------

    [Fact]
    public async Task ProcessAsync_一致するルールがあれば実際にファイルを移動しCompletedで記録される()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules = { CreateRule("pdfをdestへ", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) },
        };
        var coordinator = CreateCoordinator(settings);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        var record = Assert.Single(records);
        Assert.Equal(OperationState.Completed, record.State);
        string expectedDest = Path.Combine(_destDir, "report.pdf");
        Assert.Equal(expectedDest, record.DestinationPath);

        // 実ファイルが実際に移動していることを確認。
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(expectedDest));

        // DBにも実際にCompletedとして記録されていることを確認（in-memoryの戻り値だけでなく実DBを検証）。
        var persisted = await _realRepository.GetByIdAsync(record.Id);
        Assert.NotNull(persisted);
        Assert.Equal(OperationState.Completed, persisted!.State);
        Assert.Equal(expectedDest, persisted.DestinationPath);
    }

    [Fact]
    public async Task ProcessAsync_状態遷移はPlanned_Executing_Completedの順で記録される()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules = { CreateRule("pdfをdestへ", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) },
        };
        var recorder = new RecordingHistoryRepository(_realRepository);
        var coordinator = CreateCoordinator(settings, recorder);

        await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Equal(
            new[] { "Insert:Planned", "Update:Executing", "Update:Completed" },
            recorder.Events);
    }

    [Fact]
    public async Task ProcessAsync_一致するルールがなければ何もせずDBにも記録しない()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules = { CreateRule("docxのみ", Cond("extension", "equals", "docx"), MoveTo(_destDir)) },
        };
        var coordinator = CreateCoordinator(settings);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Empty(records);
        Assert.True(File.Exists(sourcePath)); // 何も変化していない
        Assert.Empty(await _realRepository.GetRecentAsync(10));
    }

    [Fact]
    public async Task ProcessAsync_ファイル操作が失敗した場合はFailedで記録され後続アクションは実行されない()
    {
        // 移動先ディレクトリのパスに、あらかじめ「同名のファイル」を作っておくことで
        // Directory.CreateDirectory が失敗するようにし、決定的に操作失敗を再現する。
        string blockingDestination = Path.Combine(_workDir, "blocked-destination");
        File.WriteAllText(blockingDestination, "this is a file, not a directory");

        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules =
            {
                CreateRule("失敗するはずのルール", Cond("extension", "equals", "pdf"),
                    MoveTo(blockingDestination), // 1つ目のアクションで失敗させる
                    Recycle()),                  // 2つ目は実行されないはず
            },
        };
        var coordinator = CreateCoordinator(settings);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        var record = Assert.Single(records); // Recycleアクションは実行されていない
        Assert.Equal(OperationState.Failed, record.State);
        Assert.NotNull(record.ErrorMessage);

        // Recycleが実行されていれば元ファイルは消えているはずだが、残っている＝後続が実行されなかった証拠。
        Assert.True(File.Exists(sourcePath));

        var persisted = await _realRepository.GetByIdAsync(record.Id);
        Assert.Equal(OperationState.Failed, persisted!.State);
    }

    // --- ApplyAllMatchingRules: 複数ルール・複数アクションの連鎖 --------------------------------

    [Fact]
    public async Task ProcessAsync_ApplyAllMatchingRulesがtrueなら複数ルールのアクションが順に連鎖実行される()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Settings = new AppSettings { ApplyAllMatchingRules = true },
            Rules =
            {
                CreateRule("先にリネーム", Cond("extension", "equals", "pdf"), RenameTo("renamed.pdf")),
                CreateRule("次に移動", Cond("extension", "equals", "pdf"), MoveTo(_destDir)),
            },
        };
        var coordinator = CreateCoordinator(settings);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Equal(2, records.Count);
        Assert.Equal(OperationType.Rename, records[0].OpType);
        Assert.Equal(OperationType.Move, records[1].OpType);
        Assert.All(records, r => Assert.Equal(OperationState.Completed, r.State));

        // 2番目のアクション（Move）は、1番目のアクション（Rename）後のパスに対して実行されている。
        Assert.Equal(Path.Combine(_sourceDir, "renamed.pdf"), records[1].SourcePath);

        string finalPath = Path.Combine(_destDir, "renamed.pdf");
        Assert.True(File.Exists(finalPath));
        Assert.False(File.Exists(sourcePath));
    }

    [Fact]
    public async Task ProcessAsync_ApplyAllMatchingRulesがfalseなら最優先ルールのみ実行される()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        string destA = Path.Combine(_workDir, "destA");
        string destB = Path.Combine(_workDir, "destB");
        Directory.CreateDirectory(destA);
        Directory.CreateDirectory(destB);

        var settings = new FakeSettingsRepository
        {
            Settings = new AppSettings { ApplyAllMatchingRules = false },
            Rules =
            {
                CreateRule("優先度1", Cond("extension", "equals", "pdf"), MoveTo(destA)),
                CreateRule("優先度2", Cond("extension", "equals", "pdf"), MoveTo(destB)),
            },
        };
        var coordinator = CreateCoordinator(settings);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Single(records);
        Assert.True(File.Exists(Path.Combine(destA, "report.pdf")));
        Assert.False(File.Exists(Path.Combine(destB, "report.pdf")));
    }

    // --- 同名衝突（Skip）時は後続アクションへ継続する ------------------------------------------

    [Fact]
    public async Task ProcessAsync_Skipポリシーで衝突した場合は後続アクションへ継続する()
    {
        // destDirには既に同名ファイルが存在する状態を作る。
        File.WriteAllText(Path.Combine(_destDir, "report.pdf"), "existing");
        string sourcePath = CreateSourceFile("report.pdf");

        var settings = new FakeSettingsRepository
        {
            Rules =
            {
                CreateRule("移動してからリネーム", Cond("extension", "equals", "pdf"),
                    MoveTo(_destDir),          // 衝突 → Skipで無処理
                    RenameTo("after-skip.pdf")), // 元のパスのまま実行されるはず
            },
        };
        var coordinator = CreateCoordinator(settings, defaultConflictPolicy: ConflictPolicy.Skip);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Equal(2, records.Count);
        Assert.Equal(OperationState.Completed, records[0].State);
        Assert.Null(records[0].DestinationPath);
        Assert.Equal(OperationState.Completed, records[1].State);

        string expectedFinalPath = Path.Combine(_sourceDir, "after-skip.pdf");
        Assert.True(File.Exists(expectedFinalPath));
        // 移動先の既存ファイルは上書きされていない。
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_destDir, "report.pdf")));
    }

    // --- CopyAsync実行後は対象パスが変わらない -----------------------------------------------

    [Fact]
    public async Task ProcessAsync_Copyアクション後も対象パスは元のままで後続アクションが継続する()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules =
            {
                CreateRule("コピーしてからゴミ箱送り", Cond("extension", "equals", "pdf"),
                    CopyTo(_destDir),
                    Recycle()),
            },
        };
        var coordinator = CreateCoordinator(settings);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Equal(2, records.Count);
        Assert.Equal(OperationType.Copy, records[0].OpType);
        Assert.Equal(OperationType.Recycle, records[1].OpType);
        Assert.Equal(sourcePath, records[1].SourcePath); // Copyは対象パスを変えない

        Assert.True(File.Exists(Path.Combine(_destDir, "report.pdf"))); // コピー先は残る
        Assert.False(File.Exists(sourcePath)); // ゴミ箱送りにより元ファイルは消える
    }

    // --- Phase2: OCR/AI解析パイプライン（2-1/2-2 OCR → 0-4 PythonApiClient.AnalyzeAsync） -----------

    [Fact]
    public async Task ProcessAsync_ocr_containsルールがOCR抽出結果に一致すればそのルールが適用される()
    {
        string sourcePath = CreateSourceFile("invoice.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules = { CreateRule("請求書はdestへ", Cond("ocr_contains", "contains", "請求書"), MoveTo(_destDir)) },
        };
        var ocr = new FakeOcrService { OcrTextToReturn = "株式会社サンプル 請求書 2026年8月25日" };
        var python = new FakePythonApiClient(); // AnalyzeAsyncは呼ばれるが、この検証では戻り値は使わない
        var coordinator = CreateCoordinator(settings, ocrService: ocr, pythonApiClient: python);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Single(records);
        Assert.Equal(1, ocr.ExtractTextCallCount);
        Assert.True(File.Exists(Path.Combine(_destDir, "invoice.pdf")));
    }

    [Fact]
    public async Task ProcessAsync_ai_categoryルールがPython解析結果に一致すればリネームpatternのプレースホルダーが展開される()
    {
        string sourcePath = CreateSourceFile("scan.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules =
            {
                CreateRule("請求書カテゴリはリネーム", Cond("ai_category", "equals", "invoice"),
                    RenameTo("{date}_{company}.pdf")),
            },
        };
        var ocr = new FakeOcrService { OcrTextToReturn = "株式会社サンプル 請求書 2026年8月25日" };
        var python = new FakePythonApiClient
        {
            ResponseToReturn = new AnalyzeResponse
            {
                Success = true,
                Category = "invoice",
                Metadata = new Dictionary<string, string> { ["date"] = "2026-08-25", ["company"] = "サンプル株式会社" },
            },
        };
        var coordinator = CreateCoordinator(settings, ocrService: ocr, pythonApiClient: python);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Single(records);
        Assert.Equal(1, python.AnalyzeCallCount);
        Assert.Equal(sourcePath, python.LastRequest!.FilePath);
        Assert.Equal("株式会社サンプル 請求書 2026年8月25日", python.LastRequest!.OcrText);

        string expectedPath = Path.Combine(_sourceDir, "2026-08-25_サンプル株式会社.pdf");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public async Task ProcessAsync_OCR抽出に失敗した場合はPython連携を呼ばずルールベースへフォールバックする()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules =
            {
                // 優先ルール: OCR依存（OCRが失敗するため不一致になるはず）。
                CreateRule("請求書はdestAへ", Cond("ocr_contains", "contains", "請求書"), MoveTo(Path.Combine(_workDir, "destA"))),
                // フォールバック: 拡張子のみで判定するルールベースルール。
                CreateRule("pdf全般はdestBへ", Cond("extension", "equals", "pdf"), MoveTo(Path.Combine(_workDir, "destB"))),
            },
        };
        Directory.CreateDirectory(Path.Combine(_workDir, "destB"));
        var ocr = new FakeOcrService { ThrowOnExtract = true }; // OCR実装が例外を投げるケースも想定
        var python = new FakePythonApiClient();
        var coordinator = CreateCoordinator(settings, ocrService: ocr, pythonApiClient: python);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Single(records);
        Assert.Equal(1, ocr.ExtractTextCallCount);
        Assert.Equal(0, python.AnalyzeCallCount); // OCR失敗時はPython連携自体を呼ばない
        Assert.True(File.Exists(Path.Combine(_workDir, "destB", "report.pdf")));
    }

    [Fact]
    public async Task ProcessAsync_PythonAPI呼び出しが失敗した場合はai_categoryルールへフォールバックせず後続ルールが適用される()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules =
            {
                CreateRule("invoiceカテゴリはdestAへ", Cond("ai_category", "equals", "invoice"), MoveTo(Path.Combine(_workDir, "destA"))),
                CreateRule("pdf全般はdestBへ", Cond("extension", "equals", "pdf"), MoveTo(Path.Combine(_workDir, "destB"))),
            },
        };
        Directory.CreateDirectory(Path.Combine(_workDir, "destB"));
        var ocr = new FakeOcrService { OcrTextToReturn = "何らかのテキスト" };
        var python = new FakePythonApiClient { ThrowOnAnalyze = true };
        var coordinator = CreateCoordinator(settings, ocrService: ocr, pythonApiClient: python);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Single(records);
        Assert.Equal(1, python.AnalyzeCallCount);
        Assert.True(File.Exists(Path.Combine(_workDir, "destB", "report.pdf")));
    }

    [Fact]
    public async Task ProcessAsync_言語パック未インストールの場合はOCR抽出自体を試みずルールベースへフォールバックする()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules =
            {
                CreateRule("請求書はdestAへ", Cond("ocr_contains", "contains", "請求書"), MoveTo(Path.Combine(_workDir, "destA"))),
                CreateRule("pdf全般はdestBへ", Cond("extension", "equals", "pdf"), MoveTo(Path.Combine(_workDir, "destB"))),
            },
        };
        Directory.CreateDirectory(Path.Combine(_workDir, "destB"));
        var ocr = new FakeOcrService { LanguagePackAvailable = false };
        var coordinator = CreateCoordinator(settings, ocrService: ocr, pythonApiClient: new FakePythonApiClient());

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Single(records);
        Assert.Equal(0, ocr.ExtractTextCallCount); // 言語パック未インストール検出時点でOCR自体を試みない
        Assert.True(File.Exists(Path.Combine(_workDir, "destB", "report.pdf")));
    }

    [Fact]
    public async Task ProcessAsync_どのルールもocr_containsとai_categoryを含まなければOCR_Python連携は一切呼ばれない()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules = { CreateRule("pdfをdestへ", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) },
        };
        var ocr = new FakeOcrService { OcrTextToReturn = "無視されるはずのテキスト" };
        var python = new FakePythonApiClient();
        var coordinator = CreateCoordinator(settings, ocrService: ocr, pythonApiClient: python);

        var records = await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.Single(records);
        Assert.Equal(0, ocr.ExtractTextCallCount);
        Assert.Equal(0, python.AnalyzeCallCount);
    }

    // --- 引数検証 -------------------------------------------------------------------------

    [Fact]
    public void Constructor_ruleEngineがnullの場合は例外を投げる()
    {
        Assert.Throws<ArgumentNullException>(() => new ProcessingCoordinator(
            null!, _realRepository, new FileOperationService(), new FakeSettingsRepository()));
    }

    [Fact]
    public async Task ProcessAsync_metadataがnullの場合は例外を投げる()
    {
        var coordinator = CreateCoordinator(new FakeSettingsRepository());
        await Assert.ThrowsAsync<ArgumentNullException>(() => coordinator.ProcessAsync(null!));
    }

    [Fact]
    public async Task ProcessAsync_対象ファイルが既に存在しない場合は何もしない()
    {
        var settings = new FakeSettingsRepository
        {
            Rules = { CreateRule("pdf全般", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) },
        };
        var coordinator = CreateCoordinator(settings);

        string missingPath = Path.Combine(_sourceDir, "missing.pdf");
        var metadata = new FileMetadata { FullPath = missingPath, FileName = "missing.pdf", Extension = ".pdf" };

        var records = await coordinator.ProcessAsync(metadata);

        Assert.Empty(records);
    }

    // --- ProcessingCompletedイベント ---------------------------------------------------------

    [Fact]
    public async Task ProcessingCompleted_処理完了後にイベントが発火する()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules = { CreateRule("pdfをdestへ", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) },
        };
        var coordinator = CreateCoordinator(settings);

        ProcessingCompletedEventArgs? received = null;
        coordinator.ProcessingCompleted += (_, e) => received = e;

        await coordinator.ProcessAsync(BuildMetadata(sourcePath));

        Assert.NotNull(received);
        Assert.Equal(sourcePath, received!.SourceFullPath);
        Assert.Single(received.Records);
    }

    // --- 1-5 FileStabilityDetectorとの実配線（真のエンドツーエンド） -----------------------------

    [Fact]
    public async Task AttachTo_FileStabilityDetectorの安定通知を受けて実際にファイル操作が実行される()
    {
        string sourcePath = CreateSourceFile("report.pdf");
        var settings = new FakeSettingsRepository
        {
            Rules = { CreateRule("pdfをdestへ", Cond("extension", "equals", "pdf"), MoveTo(_destDir)) },
        };
        var coordinator = CreateCoordinator(settings);

        using var detector = new FileStabilityDetector(stabilityCheckIntervalMs: 30);
        coordinator.AttachTo(detector);

        var tcs = new TaskCompletionSource<ProcessingCompletedEventArgs>();
        coordinator.ProcessingCompleted += (_, e) => tcs.TrySetResult(e);

        detector.Enqueue(sourcePath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));
        Assert.True(ReferenceEquals(tcs.Task, completed), "タイムアウトしました。");

        var result = await tcs.Task;
        Assert.Single(result.Records);
        Assert.Equal(OperationState.Completed, result.Records[0].State);

        string expectedDest = Path.Combine(_destDir, "report.pdf");
        Assert.True(File.Exists(expectedDest));
        Assert.False(File.Exists(sourcePath));
    }
}
