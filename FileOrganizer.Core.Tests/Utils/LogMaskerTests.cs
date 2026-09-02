using FileOrganizer.Core.Utils;

namespace FileOrganizer.Core.Tests.Utils;

/// <summary>
/// 仕様書§7.2-7「診断ログ: ワンクリック出力されるサポートログは、ファイル名・個人情報が
/// ハッシュ化/マスクされていること」の受け入れ基準を検証する。
/// 対象: <see cref="LogMasker"/>。
/// <see cref="MaskPersonalInfo_長文はOCR全文の混入防止のため原文を含まない完全代替文字列になる"/>系は、
/// <c>OcrPrivacyTests</c>（DB永続化経路にOCR全文が一切現れないことのリフレクション/実スキーマ検証）と
/// 同様の趣旨で、「LogMaskerの出力にOCR全文が一切含まれないこと」を実行時に直接保証する回帰ガード。
/// </summary>
public class LogMaskerTests
{
    // --- HashPath / HashFileName: パス・ファイル名のハッシュ化 ------------------------

    [Fact]
    public void HashPath_同一パスは常に同一ハッシュになる()
    {
        string a = LogMasker.HashPath(@"C:\Demo\Inbox\invoice_2026.pdf");
        string b = LogMasker.HashPath(@"C:\Demo\Inbox\invoice_2026.pdf");

        Assert.Equal(a, b);
    }

    [Fact]
    public void HashPath_異なるパスは異なるハッシュになる()
    {
        string a = LogMasker.HashPath(@"C:\Demo\Inbox\invoice_2026.pdf");
        string b = LogMasker.HashPath(@"C:\Demo\Inbox\contract_2026.pdf");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashPath_区切り文字の違いだけなら同一ハッシュになる()
    {
        // Windows実パス(\)とスラッシュ表記(/)の揺れを吸収し、同一ファイルとして突き合わせ可能にする。
        string backslash = LogMasker.HashPath(@"C:\Demo\Inbox\invoice_2026.pdf");
        string slash = LogMasker.HashPath("C:/Demo/Inbox/invoice_2026.pdf");

        Assert.Equal(backslash, slash);
    }

    [Fact]
    public void HashPath_出力に元のパス文字列が含まれない()
    {
        string path = @"C:\Users\yamada-taro\Documents\履歴書_山田太郎.pdf";
        string hashed = LogMasker.HashPath(path);

        Assert.DoesNotContain("yamada-taro", hashed);
        Assert.DoesNotContain("山田太郎", hashed);
        Assert.DoesNotContain(path, hashed);
        Assert.StartsWith("sha256:", hashed);
    }

    [Fact]
    public void HashPath_null又は空文字は空文字を返す()
    {
        Assert.Equal(string.Empty, LogMasker.HashPath(null));
        Assert.Equal(string.Empty, LogMasker.HashPath(string.Empty));
    }

    [Fact]
    public void HashFileName_拡張子は保持しつつファイル名本体のみハッシュ化される()
    {
        string hashed = LogMasker.HashFileName("山田太郎_履歴書.pdf");

        Assert.EndsWith(".pdf", hashed);
        Assert.DoesNotContain("山田太郎", hashed);
        Assert.DoesNotContain("履歴書", hashed);
    }

    [Fact]
    public void HashFileName_同名ファイルは同一ハッシュになる()
    {
        Assert.Equal(LogMasker.HashFileName("a.txt"), LogMasker.HashFileName("a.txt"));
        Assert.NotEqual(LogMasker.HashFileName("a.txt"), LogMasker.HashFileName("b.txt"));
    }

    // --- MaskPersonalInfo: 住所・氏名らしき文字列パターンのマスク ----------------------

    [Theory]
    [InlineData("東京都渋谷区代々木2-3-1に発送してください", "東京都渋谷区代々木2-3-1")]
    [InlineData("大阪府大阪市北区梅田1丁目のオフィスです", "大阪府大阪市北区梅田1丁目")]
    public void MaskPersonalInfo_都道府県から始まる住所らしき文字列はマスクされる(string input, string shouldNotContain)
    {
        string masked = LogMasker.MaskPersonalInfo(input);

        Assert.DoesNotContain(shouldNotContain, masked);
        Assert.Contains("[MASKED]", masked);
    }

    [Fact]
    public void MaskPersonalInfo_郵便番号はマスクされる()
    {
        string masked = LogMasker.MaskPersonalInfo("〒150-0041までお送りします");

        Assert.DoesNotContain("150-0041", masked);
        Assert.Contains("[MASKED]", masked);
    }

    [Theory]
    [InlineData("山田太郎様", "山田太郎")]
    [InlineData("鈴木花子さん", "鈴木花子")]
    public void MaskPersonalInfo_敬称付きの氏名らしき文字列はマスクされる(string input, string shouldNotContain)
    {
        string masked = LogMasker.MaskPersonalInfo(input);

        Assert.DoesNotContain(shouldNotContain, masked);
        Assert.Contains("[MASKED]", masked);
    }

    [Fact]
    public void MaskPersonalInfo_ラベル付きの氏名住所はマスクされる()
    {
        string masked = LogMasker.MaskPersonalInfo("氏名: 田中一郎 / 住所: 東京都新宿区西新宿");

        Assert.DoesNotContain("田中一郎", masked);
        Assert.Contains("[MASKED]", masked);
    }

    [Fact]
    public void MaskPersonalInfo_電話番号はマスクされる()
    {
        string masked = LogMasker.MaskPersonalInfo("担当まで090-1234-5678へご連絡ください");

        Assert.DoesNotContain("090-1234-5678", masked);
        Assert.Contains("[MASKED]", masked);
    }

    [Fact]
    public void MaskPersonalInfo_メールアドレスはマスクされる()
    {
        string masked = LogMasker.MaskPersonalInfo("連絡先: taro.yamada@example.com です");

        Assert.DoesNotContain("taro.yamada@example.com", masked);
        Assert.Contains("[MASKED]", masked);
    }

    [Fact]
    public void MaskPersonalInfo_個人情報らしき文字列を含まない場合はそのまま返る()
    {
        string input = "invoice.pdf を Documents/Invoices へ移動しました";
        string masked = LogMasker.MaskPersonalInfo(input);

        Assert.Equal(input, masked);
    }

    [Fact]
    public void MaskPersonalInfo_null又は空文字は空文字を返す()
    {
        Assert.Equal(string.Empty, LogMasker.MaskPersonalInfo(null));
        Assert.Equal(string.Empty, LogMasker.MaskPersonalInfo(string.Empty));
    }

    // --- 回帰ガード: OCR全文がLogMaskerの出力に一切含まれないこと ----------------------
    // OcrPrivacyTests（DB永続化モデル/実DBスキーマにOCR全文用の経路が存在しないことの検証）と
    // 同様の趣旨で、「万一OCR全文がLogMaskerへ渡っても出力に原文が残らない」ことを直接検証する。

    /// <summary>実際のOCR抽出結果を模した、境界（<see cref="LogMasker.MaxMaskedTextLength"/>）超の長文。</summary>
    private const string SimulatedOcrFullText =
        "請求書\n" +
        "発行元: 株式会社サンプルコーポレーション\n" +
        "担当者: 山田太郎様\n" +
        "住所: 東京都渋谷区代々木2-3-1 サンプルビル5F\n" +
        "郵便番号: 〒150-0041\n" +
        "電話番号: 03-1234-5678\n" +
        "メール: taro.yamada@example.com\n" +
        "請求日: 2026年8月31日\n" +
        "請求金額: 3,980円\n" +
        "備考: 本書面はOCRにより読み取られた全文のサンプルであり、" +
        "個人情報を含む長文がそのままログへ出力されてはならないことを検証するためのテストデータです。";

    [Fact]
    public void MaskPersonalInfo_長文はOCR全文の混入防止のため原文を含まない完全代替文字列になる()
    {
        Assert.True(SimulatedOcrFullText.Length > LogMasker.MaxMaskedTextLength,
            "テストデータがMaxMaskedTextLengthを超えていない（テスト前提が崩れている）。");

        string masked = LogMasker.MaskPersonalInfo(SimulatedOcrFullText);

        // 原文全体はもちろん、原文中の断片（マスク対象外の一般語句を含む）も一切含まれないこと。
        Assert.DoesNotContain(SimulatedOcrFullText, masked);
        Assert.DoesNotContain("株式会社サンプルコーポレーション", masked);
        Assert.DoesNotContain("山田太郎", masked);
        Assert.DoesNotContain("本書面はOCRにより読み取られた全文のサンプルであり", masked);
        Assert.StartsWith("[REDACTED:", masked);
    }

    [Fact]
    public void MaskPersonalInfo_長文は代替文字列自体もMaxMaskedTextLength以下に収まる()
    {
        // 代替文字列（メタ情報のみ）がMaxMaskedTextLengthを超えないことを確認し、
        // 「短い自由記述専用」というLogMaskerの前提を代替文字列自身も破らないことを保証する。
        string masked = LogMasker.MaskPersonalInfo(SimulatedOcrFullText);

        Assert.True(masked.Length <= LogMasker.MaxMaskedTextLength);
    }

    [Fact]
    public void HashPath_OCR全文相当の長い文字列を渡しても出力に原文は含まれない()
    {
        // HashPathはパス用だが、誤ってOCR全文相当の長文が渡されても
        // 一方向ハッシュである以上、原文の痕跡が出力に残らないことを確認する。
        string hashed = LogMasker.HashPath(SimulatedOcrFullText);

        Assert.DoesNotContain(SimulatedOcrFullText, hashed);
        Assert.DoesNotContain("山田太郎", hashed);
        Assert.DoesNotContain("株式会社サンプルコーポレーション", hashed);
    }
}
