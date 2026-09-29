using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Arcadia.App.ViewModels.Games;

namespace Arcadia.App.Views.Games;

/// <summary>
/// Library view. Code-behind derives the card column count and card art height (2:3 box-art proportion) from
/// the available width, and chunks the games into rows so the grid can virtualize like the list does.
/// </summary>
public partial class GamesView : UserControl
{
    private const double CardMinWidth = 196;
    private const double CardChrome = 12 + 2 + 12; // outer margin, frame, art inset

    public static readonly DependencyProperty CardColumnsProperty = DependencyProperty.Register(
        nameof(CardColumns), typeof(int), typeof(GamesView), new PropertyMetadata(6, (d, _) => ((GamesView)d).RebuildRows()));

    public static readonly DependencyProperty CardArtHeightProperty = DependencyProperty.Register(
        nameof(CardArtHeight), typeof(double), typeof(GamesView), new PropertyMetadata(270.0));

    public static readonly DependencyProperty CardsProperty = DependencyProperty.Register(
        nameof(Cards), typeof(IReadOnlyList<GameCardViewModel>), typeof(GamesView), new PropertyMetadata(null, (d, _) => ((GamesView)d).RebuildRows()));

    private static readonly DependencyPropertyKey CardRowsKey = DependencyProperty.RegisterReadOnly(
        nameof(CardRows), typeof(IReadOnlyList<GameCardViewModel[]>), typeof(GamesView), new PropertyMetadata(Array.Empty<GameCardViewModel[]>()));

    public static readonly DependencyProperty CardRowsProperty = CardRowsKey.DependencyProperty;

    public int CardColumns { get => (int)GetValue(CardColumnsProperty); set => SetValue(CardColumnsProperty, value); }
    public double CardArtHeight { get => (double)GetValue(CardArtHeightProperty); set => SetValue(CardArtHeightProperty, value); }
    public IReadOnlyList<GameCardViewModel>? Cards { get => (IReadOnlyList<GameCardViewModel>?)GetValue(CardsProperty); set => SetValue(CardsProperty, value); }
    public IReadOnlyList<GameCardViewModel[]> CardRows => (IReadOnlyList<GameCardViewModel[]>)GetValue(CardRowsProperty);

    public GamesView()
    {
        InitializeComponent();
        SetBinding(CardsProperty, new Binding(nameof(GamesViewModel.Items)));
        CardList.SizeChanged += (_, _) =>
        {
            var available = CardList.ActualWidth - 10 + 12; // scrollbar allowance, negative item margins
            if (available <= 0) return;
            CardColumns = Math.Max(1, (int)(available / CardMinWidth));
            var artWidth = available / CardColumns - CardChrome;
            CardArtHeight = Math.Round(Math.Max(120, artWidth * 1.5));
        };
    }

    private void RebuildRows() => SetValue(CardRowsKey, Chunk(Cards ?? [], Math.Max(1, CardColumns)));

    internal static IReadOnlyList<GameCardViewModel[]> Chunk(IReadOnlyList<GameCardViewModel> cards, int columns) =>
        cards.Chunk(columns).ToList();
}
