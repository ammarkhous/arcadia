using CommunityToolkit.Mvvm.ComponentModel;

namespace Arcadia.App.Dialogs;

/// <summary>Base for view models shown in <see cref="DialogWindow"/>. The view is resolved by DataTemplate.</summary>
public abstract partial class DialogViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;

    public virtual string Icon => "Logo";
    public virtual double DialogWidth => 560;

    public event EventHandler<bool>? CloseRequested;

    /// <summary>Called once the dialog is visible (e.g. to start loading).</summary>
    public virtual Task OnShownAsync() => Task.CompletedTask;

    protected void Close(bool accepted) => CloseRequested?.Invoke(this, accepted);
}
