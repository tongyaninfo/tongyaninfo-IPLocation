using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using IpLocation.Api.Models.Responses;
using Microsoft.AspNetCore.Mvc;

namespace IpLocation.Api.Common;

/// <summary>三种表示直接来自同一 DTO，不分别维护或推导查询语义。</summary>
public static class QueryFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = ApiJsonEncoding.Encoder
    };

    public static bool IsSupported(string format) => format is "json" or "xml" or "txt";

    public static ContentResult Format(QueryResponse response, string format)
    {
        var data = JsonSerializer.SerializeToElement(response.Result, JsonOptions);
        return new ContentResult
        {
            StatusCode = response.StatusCode,
            ContentType = format switch
            {
                "xml" => "application/xml; charset=utf-8",
                "txt" => "text/plain; charset=utf-8",
                _ => "application/json; charset=utf-8"
            },
            Content = format switch
            {
                "xml" => new XDocument(new XDeclaration("1.0", "utf-8", null), XmlElement("ApiResult", data)).ToString(),
                "txt" => ToText(data),
                _ => data.GetRawText()
            }
        };
    }

    private static XElement XmlElement(string name, JsonElement value)
    {
        var element = new XElement(name);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject()) element.Add(XmlElement(property.Name, property.Value));
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) element.Add(XmlElement("item", item));
                break;
            case JsonValueKind.Null:
                element.Add(new XAttribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "nil", true));
                break;
            default:
                element.Value = Scalar(value);
                break;
        }

        return element;
    }

    private static string ToText(JsonElement result)
    {
        var text = new StringBuilder();
        foreach (var property in result.EnumerateObject())
        {
            if (property.Name == "data" && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in property.Value.EnumerateObject()) Write(field.Name, field.Value);
            }
            else Write(property.Name, property.Value);
        }

        return text.ToString();

        void Write(string key, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject()) Write($"{key}.{property.Name}", property.Value);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var sourceId)
                        ? sourceId.GetString() : null;
                    Write($"{key}.{id ?? (index++).ToString(CultureInfo.InvariantCulture)}", item);
                }
            }
            else text.Append(key).Append('=').Append(Escape(Scalar(value))).Append('\n');
        }
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "null",
        _ => value.GetRawText()
    };

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\r", "\\r")
        .Replace("\n", "\\n").Replace("=", "\\=");
}
