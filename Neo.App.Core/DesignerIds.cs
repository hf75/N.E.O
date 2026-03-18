using System;
using System.Globalization;

namespace Neo.App
{
    public static class DesignerIds
    {
        public const string NamePrefix = "__neo_";
        public const string TagPrefix = "__neo:id=";

        public static bool IsDesignIdValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.StartsWith(NamePrefix, StringComparison.Ordinal) ||
                   value.StartsWith(TagPrefix, StringComparison.Ordinal);
        }

        public static bool TryParseDesignNumber(string value, out int number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string? suffix = null;
            if (value.StartsWith(NamePrefix, StringComparison.Ordinal))
                suffix = value.Substring(NamePrefix.Length);
            else if (value.StartsWith(TagPrefix, StringComparison.Ordinal))
                suffix = value.Substring(TagPrefix.Length);

            if (string.IsNullOrWhiteSpace(suffix)) return false;
            return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }

        public static string CreateNameId(int number) => $"{NamePrefix}{number:D4}";
        public static string CreateTagId(int number) => $"{TagPrefix}{number:D4}";
    }
}