namespace Maclar.Web.Models;

/// <summary>
/// Aynı maçın (aynı anahtar) saat veya salon bilgisinin değişmesi.
/// </summary>
public sealed record MatchChange(MatchDto Old, MatchDto Current);

/// <summary>
/// Bir senkronizasyonun, bir öncekine göre farkları.
/// </summary>
/// <param name="IsFirstSync">Veritabanı boştu; karşılaştırılacak önceki veri yok.</param>
/// <param name="Added">Veritabanında hiç olmayan yeni maçlar.</param>
/// <param name="Changed">Saat veya salonu değişen maçlar.</param>
/// <param name="Disappeared">Önceki senkronizasyonda sitede olup bu sefer gelmeyen maçlar.</param>
public sealed record MatchSaveResult(
    bool IsFirstSync,
    IReadOnlyList<MatchDto> Added,
    IReadOnlyList<MatchChange> Changed,
    IReadOnlyList<MatchDto> Disappeared);
