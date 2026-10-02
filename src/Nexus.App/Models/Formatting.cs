using System.Globalization;

namespace Nexus.App.Models;

public static class Formatting
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string[] SizeUnits = ["Б", "КБ", "МБ", "ГБ", "ТБ"];

    public static string Size(long? bytes)
    {
        if (bytes is null)
        {
            return string.Empty;
        }

        double size = bytes.Value;
        var unit = 0;
        while (size >= 1024 && unit < SizeUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0} {SizeUnits[unit]}"
            : string.Format(Russian, "{0:0.#} {1}", size, SizeUnits[unit]);
    }

    public static string DateTime(DateTimeOffset value) =>
        value.LocalDateTime.ToString("dd.MM.yyyy HH:mm", Russian);

    public static string RelativeDate(DateTimeOffset value, DateTimeOffset now)
    {
        var local = value.LocalDateTime;
        var today = now.LocalDateTime.Date;
        if (local.Date == today)
        {
            return $"Сегодня, {local:HH:mm}";
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"Вчера, {local:HH:mm}";
        }

        return local.ToString(local.Year == today.Year ? "d MMMM, HH:mm" : "d MMMM yyyy", Russian);
    }

    public static string Count(int count, string one, string few, string many)
    {
        var mod100 = count % 100;
        var mod10 = count % 10;
        var word = mod100 is >= 11 and <= 14
            ? many
            : mod10 switch
            {
                1 => one,
                >= 2 and <= 4 => few,
                _ => many
            };
        return $"{count.ToString("N0", Russian)} {word}";
    }

    public static string Items(int count) => Count(count, "элемент", "элемента", "элементов");
}
