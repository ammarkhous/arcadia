using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Arcadia.App.Views.Travel;

/// <summary>Memories as chapters (a trip, or a month of loose moments): a hero photo, a strip, and the words and footsteps.</summary>
public partial class TravelMemoriesView : UserControl
{
    public TravelMemoriesView() => InitializeComponent();

    public static readonly DependencyProperty ChaptersProperty = DependencyProperty.Register(nameof(Chapters), typeof(IList), typeof(TravelMemoriesView));
    public IList? Chapters { get => (IList?)GetValue(ChaptersProperty); set => SetValue(ChaptersProperty, value); }

    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(TravelMemoriesView));
    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }

    public static readonly DependencyProperty OpenTripCommandProperty = DependencyProperty.Register(nameof(OpenTripCommand), typeof(ICommand), typeof(TravelMemoriesView));
    public ICommand? OpenTripCommand { get => (ICommand?)GetValue(OpenTripCommandProperty); set => SetValue(OpenTripCommandProperty, value); }

    public static readonly DependencyProperty ShowHeaderProperty = DependencyProperty.Register(nameof(ShowHeader), typeof(bool), typeof(TravelMemoriesView), new PropertyMetadata(true));
    public bool ShowHeader { get => (bool)GetValue(ShowHeaderProperty); set => SetValue(ShowHeaderProperty, value); }
}
