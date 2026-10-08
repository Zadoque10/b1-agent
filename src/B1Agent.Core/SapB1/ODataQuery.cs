namespace B1Agent.Core.SapB1;

/// <summary>
/// Small helpers for building Service Layer OData URLs safely.
/// Every value that comes from the LLM (and therefore, indirectly, from the user) goes through <see cref="Literal"/>.
/// </summary>
public static class ODataQuery
{
    /// <summary>Quotes a value as an OData string literal, doubling embedded single quotes.</summary>
    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Builds "Entity?$select=...&$filter=...&$orderby=...&$top=..." with URL-encoded option values.</summary>
    public static string Build(string resource, string? select = null, string? filter = null, string? orderBy = null, int? top = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(select)) parts.Add("$select=" + Uri.EscapeDataString(select));
        if (!string.IsNullOrEmpty(filter)) parts.Add("$filter=" + Uri.EscapeDataString(filter));
        if (!string.IsNullOrEmpty(orderBy)) parts.Add("$orderby=" + Uri.EscapeDataString(orderBy));
        if (top is > 0) parts.Add("$top=" + top.Value);
        return parts.Count == 0 ? resource : resource + "?" + string.Join('&', parts);
    }

    /// <summary>Entity key segment, e.g. BusinessPartners('C20000').</summary>
    public static string Key(string resource, string key) =>
        resource + "(" + Uri.EscapeDataString(Literal(key)) + ")";
}
