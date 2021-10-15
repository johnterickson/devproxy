using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DevProxy
{
    public static class JsonHelperExtensions
    {
        public static List<T> ParseJsonArray<T>(this object arrayElementObj)
        {
            if(arrayElementObj is JsonElement arrayElement)
            {
                if(arrayElement.ValueKind != JsonValueKind.Array)
                {
                    throw new ArgumentException($"Not a JSON array:`{arrayElement.GetRawText()}`");
                }
                return JsonSerializer.Deserialize<List<T>>(arrayElement.GetRawText());
            }
            else if (arrayElementObj is IEnumerable<T> enumerable)
            {
                return enumerable.ToList();
            }
            else
            {
                throw new ArgumentException(
                    $"Not a JSON array or a {typeof(IEnumerable<T>).FullName}: {arrayElementObj.GetType().FullName}");
            }
        }
    }
}
