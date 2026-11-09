namespace Kql.Net.EntityFrameworkCore;

public static class KqlContextExtensions
{
    /// <summary>
    /// Enables EF Core-specific translations, so operators such as <c>contains</c>, <c>has</c>, <c>=~</c>,
    /// <c>in~</c>, <c>startswith</c> and functions such as <c>bin()</c>, <c>tolower()</c> and <c>strlen()</c>
    /// run in the database instead of in memory. Register DbSets with <see cref="KqlContext.AddTable{T}(string, IQueryable{T})"/>.
    /// </summary>
    public static KqlContext UseEntityFrameworkCore(this KqlContext context) =>
        context.AddTranslator(EfCoreKqlTranslator.Instance);
}
