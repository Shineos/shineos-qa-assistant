using Xunit;

namespace ShineosQA.Backend.Tests;

/// <summary>Windows.Media.Ocr を使うテストの直列化。WinRT OCR は同時実行すると
/// 認識結果が不安定になる実測（全体並列実行時に OcrPdfFallback が数字を読めず失敗、
/// 単独実行では成功）があるため、OCR 系テストは他のテストとも並列実行しない。
/// 機能自体の検証内容は変えない（テスト分離のみ）。</summary>
[CollectionDefinition("Windows OCR", DisableParallelization = true)]
public sealed class WindowsOcrCollection
{
}
