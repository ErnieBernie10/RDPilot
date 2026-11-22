using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using Avalonia.Media;
using RDPilot.Client.Models;

namespace RDPilot.Client.Views;

public static class ConnectionMarks
{
    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#3E6FB0")),
        new SolidColorBrush(Color.Parse("#2E8C80")),
        new SolidColorBrush(Color.Parse("#7657B3")),
        new SolidColorBrush(Color.Parse("#B0574A")),
        new SolidColorBrush(Color.Parse("#8C7A2B")),
        new SolidColorBrush(Color.Parse("#A24A7A")),
        new SolidColorBrush(Color.Parse("#4F6E8F")),
        new SolidColorBrush(Color.Parse("#5F8A3A")),
    ];

    public static readonly IValueConverter Monogram =
        new FuncValueConverter<string?, string>(name => ToMonogram(name ?? ""));

    public static readonly IValueConverter Brush =
        new FuncValueConverter<string?, IBrush>(key => Palette[StableIndex(key ?? "", Palette.Length)]);

    public static readonly IValueConverter Address =
        new FuncValueConverter<SavedConnection?, string>(connection => connection == null ? "" : ToAddress(connection));

    internal static string ToMonogram(string name)
    {
        var label = name.Trim();
        var firstDot = label.IndexOf('.');
        if (firstDot > 0 && label.Any(char.IsLetter)) label = label[..firstDot];

        var tokens = Regex.Split(label, @"[^\p{L}\p{N}]+").Where(token => token.Length > 0).ToArray();
        if (tokens.Length == 0) return "?";

        var last = tokens[^1];
        if (last.All(char.IsDigit))
        {
            var number = last.TrimStart('0');
            if (number.Length == 0) number = "0";
            if (tokens.Length == 1 || !char.IsLetter(tokens[0][0])) return number.Length > 3 ? number[^3..] : number;
            return string.Concat(char.ToUpperInvariant(tokens[0][0]), number.Length > 2 ? number[^2..] : number);
        }

        var first = tokens[0];
        if (tokens.Length > 1 && char.IsLetter(tokens[1][0]))
        {
            return string.Concat(first[0], tokens[1][0]).ToUpperInvariant();
        }

        return (first.Length > 1 ? first[..2] : first).ToUpperInvariant();
    }

    internal static string ToAddress(SavedConnection connection)
    {
        var host = connection.Port == 3389 ? connection.Host : $"{connection.Host}:{connection.Port}";
        var showHost = !string.Equals(connection.Host, connection.Name, StringComparison.OrdinalIgnoreCase) || connection.Port != 3389;
        if (string.IsNullOrWhiteSpace(connection.Username)) return showHost ? host : "";
        return showHost ? $"{connection.Username}@{host}" : connection.Username;
    }

    private static int StableIndex(string key, int modulo)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in key)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return (int)(hash % (uint)modulo);
        }
    }
}
