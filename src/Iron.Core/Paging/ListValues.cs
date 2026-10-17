using System.Globalization;

namespace Iron.Core.Paging;

/// <summary>Converts the text of a filter value to the type of the field, in the invariant culture. Shared by the parser and the data layer.</summary>
public static class ListValues
{
    public static bool IsOrdered(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        return target == typeof(DateTimeOffset) || target == typeof(DateTime) || target == typeof(DateOnly) || target == typeof(TimeOnly)
            || Type.GetTypeCode(target) is TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.Byte
                or TypeCode.Decimal or TypeCode.Double or TypeCode.Single;
    }

    public static bool TryParse(Type type, string text, out object? value)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(text);
        value = null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        var culture = CultureInfo.InvariantCulture;

        if (target == typeof(string))
        {
            value = text;
            return true;
        }

        if (target.IsEnum)
        {
            if (Enum.TryParse(target, text, ignoreCase: true, out var parsedEnum) && parsedEnum is not null && Enum.IsDefined(target, parsedEnum))
            {
                value = parsedEnum;
                return true;
            }

            return false;
        }

        if (target == typeof(Guid))
        {
            return Set(Guid.TryParse(text, out var guid), guid, out value);
        }

        if (target == typeof(bool))
        {
            return Set(bool.TryParse(text, out var boolean), boolean, out value);
        }

        if (target == typeof(DateTimeOffset))
        {
            return Set(DateTimeOffset.TryParse(text, culture, DateTimeStyles.AssumeUniversal, out var moment), moment, out value);
        }

        if (target == typeof(DateTime))
        {
            return Set(DateTime.TryParse(text, culture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment), moment, out value);
        }

        if (target == typeof(DateOnly))
        {
            return Set(DateOnly.TryParse(text, culture, DateTimeStyles.None, out var date), date, out value);
        }

        if (target == typeof(TimeOnly))
        {
            return Set(TimeOnly.TryParse(text, culture, DateTimeStyles.None, out var time), time, out value);
        }

        switch (Type.GetTypeCode(target))
        {
            case TypeCode.Int32:
                return Set(int.TryParse(text, NumberStyles.Integer, culture, out var int32), int32, out value);
            case TypeCode.Int64:
                return Set(long.TryParse(text, NumberStyles.Integer, culture, out var int64), int64, out value);
            case TypeCode.Int16:
                return Set(short.TryParse(text, NumberStyles.Integer, culture, out var int16), int16, out value);
            case TypeCode.Byte:
                return Set(byte.TryParse(text, NumberStyles.Integer, culture, out var unsignedByte), unsignedByte, out value);
            case TypeCode.Decimal:
                return Set(decimal.TryParse(text, NumberStyles.Number, culture, out var dec), dec, out value);
            case TypeCode.Double:
                return Set(double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, culture, out var dbl), dbl, out value);
            case TypeCode.Single:
                return Set(float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, culture, out var flt), flt, out value);
            default:
                return false;
        }
    }

    private static bool Set<TValue>(bool parsed, TValue parsedValue, out object? value)
    {
        value = parsed ? parsedValue : null;
        return parsed;
    }
}
