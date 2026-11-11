namespace VibeTun.Utils;

/// <summary>
/// Выбор архива ядра под Windows из файлов релиза. Вынесен отдельно от загрузки, чтобы правило
/// проверялось тестом: в релизах рядом лежат архивы-варианты той же архитектуры
/// («-legacy-windows-7», «-purego»), и по алфавиту они идут раньше основного.
/// </summary>
public static class CoreAssetPicker
{
    /// <param name="names">Имена .zip-архивов релиза под Windows, в порядке релиза.</param>
    /// <param name="arch">Архитектура в терминах Go: amd64 / arm64 / 386.</param>
    /// <returns>Индекс выбранного архива; -1, если список пуст.</returns>
    public static int Pick(IReadOnlyList<string> names, string arch)
    {
        // основной архив архитектуры: имя кончается на «-windows-{arch}.zip», без суффикса варианта
        bool Exact(string name) => name.EndsWith($"-windows-{arch}.zip", StringComparison.OrdinalIgnoreCase);
        // вариант той же архитектуры: «-windows-{arch}-legacy-windows-7.zip», «-windows-{arch}-purego.zip»
        bool Variant(string name) => name.Contains($"-windows-{arch}-", StringComparison.OrdinalIgnoreCase);
        bool Plain(string name) => !name.Contains("purego", StringComparison.OrdinalIgnoreCase);

        var index = Find(names, Exact);
        if (index < 0) index = Find(names, name => Variant(name) && Plain(name));
        if (index < 0) index = Find(names, Variant);
        // под нашу архитектуру ничего нет: берём, что есть (x64 запускает и 386, ARM64 — и amd64)
        if (index < 0) index = Find(names, Plain);
        if (index < 0 && names.Count > 0) index = 0;
        return index;
    }

    private static int Find(IReadOnlyList<string> names, Func<string, bool> match)
    {
        for (var i = 0; i < names.Count; i++)
            if (match(names[i])) return i;
        return -1;
    }
}
