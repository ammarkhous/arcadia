namespace Arcadia.App.Messages;

/// <summary>Broadcast (WeakReferenceMessenger) whenever library data changes, so every open view can refresh.</summary>
public sealed record LibraryChangedMessage(int? GameId = null, bool Deleted = false);
