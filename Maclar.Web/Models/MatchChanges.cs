namespace Maclar.Web.Models;

/// <summary>
/// Aynı maçın (aynı anahtar) saat veya salon bilgisinin değişmesi.
/// </summary>
public sealed record MatchChange(MatchDto Old, MatchDto Current);

/// <summary>
/// Bir takvim senkronizasyonunun, bir öncekine göre farkları.
/// </summary>
/// <param name="IsFirstSync">Takvimde daha önce görülmüş kayıt yok; karşılaştırılacak önceki veri yok.</param>
/// <param name="Added">Veritabanında hiç olmayan yeni maçlar.</param>
/// <param name="Changed">Saat veya salonu değişen maçlar.</param>
/// <param name="Disappeared">Önceki senkronizasyonda sitede olup bu sefer gelmeyen maçlar.</param>
public sealed record MatchSaveResult(
    bool IsFirstSync,
    IReadOnlyList<MatchDto> Added,
    IReadOnlyList<MatchChange> Changed,
    IReadOnlyList<MatchDto> Disappeared);

/// <summary>
/// Takvim dışı bir kaynağın (arşiv / Excel) kayıt sonucu.
/// </summary>
/// <param name="Added">Veritabanında olmayan, yeni eklenen maçlar.</param>
/// <param name="Updated">Zaten var olan (güncellenen) maç sayısı.</param>
/// <param name="NewlyFinished">Daha önce skoru olmayan, bu kaynakla skoru gelen maçlar.</param>
/// <param name="TimeDiffers">Kayıttaki saatten farklı saatle gelen maçlar (kayıttaki saat korunur).</param>
/// <param name="DateDiffers">Excel: aynı maç başka günde kayıtlı (ertelenmiş olabilir); yeni kayıt açılmadı.</param>
/// <param name="Replaced">Sadece Excel'den gelmiş, başka tarihli olduğu için silinen kopya sayısı
/// (arşivde: gerçek tarih geldi; Excel'de: yeni program tarihi değiştirdi).</param>
public sealed record ExternalSaveResult(
    IReadOnlyList<MatchDto> Added,
    int Updated,
    IReadOnlyList<MatchDto> NewlyFinished,
    IReadOnlyList<MatchChange> TimeDiffers,
    IReadOnlyList<MatchChange> DateDiffers,
    int Replaced);
