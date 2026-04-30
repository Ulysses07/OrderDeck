using System.Data;

namespace LiveDeck.Core.Storage;

/// <summary>Abstracts ADO.NET connection creation so tests can inject in-memory SQLite.</summary>
public interface IDbConnectionFactory
{
    IDbConnection Open();
}
