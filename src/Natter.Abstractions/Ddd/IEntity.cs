namespace Natter.Abstractions.Ddd;

/// <summary>Marker for an object that has an identity.</summary>
public interface IEntity
{
    object GetIdentity();
}

/// <summary>An object defined by its identity, not by its attributes.</summary>
public interface IEntity<out TId> : IEntity
    where TId : notnull
{
    TId Id { get; }
}
