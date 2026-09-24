using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsMapsSample.Models;
using WinUIEx.Maps;

namespace WindowsMapsSample;

public sealed partial class MainPage
{
    private sealed class SearchSession(string query, ObservableCollection<Place> results)
    {
        internal string Query { get; } = query;
        internal ObservableCollection<Place> Results { get; } = results;
        internal Place? SelectedPlace { get; set; }
    }

    private readonly Dictionary<TabViewItem, SearchSession> _searchSessions = [];
    private bool _changingSearchTabs;
    private TabViewItem? _routeTab;
    private TabViewItem? _pinTab;
    private TabViewItem? _displayedTab;

    private void SelectRouteTab()
    {
        _changingSearchTabs = true;
        if (_routeTab is null)
        {
            _routeTab = new TabViewItem { IsClosable = true, MaxWidth = 240 };
            SearchTabs.TabItems.Add(_routeTab);
        }
        _routeTab.Header = $"To {_destination!.Name}";
        SearchTabs.Visibility = Visibility.Visible;
        _displayedTab = _routeTab;
        SearchTabs.SelectedItem = _routeTab;
        _changingSearchTabs = false;
        ShowPane(DirectionsPane, "Directions");
    }

    private TabViewItem AddSearchTab(string query, ObservableCollection<Place> results)
    {
        var tab = new TabViewItem { Header = query, IsClosable = true, MaxWidth = 240 };
        _searchSessions.Add(tab, new SearchSession(query, results));
        _changingSearchTabs = true;
        _displayedTab = tab;
        SearchTabs.TabItems.Add(tab);
        SearchTabs.Visibility = Visibility.Visible;
        SearchTabs.SelectedItem = tab;
        _changingSearchTabs = false;
        return tab;
    }

    private void DeselectSearchTab()
    {
        _changingSearchTabs = true;
        _displayedTab = null;
        SearchTabs.SelectedItem = null;
        _changingSearchTabs = false;
    }

    private void RememberSelectedPlace(Place place)
    {
        if (_changingSearchTabs) return;
        if (SearchTabs.SelectedItem is TabViewItem searchTab && searchTab != _pinTab &&
            _searchSessions.TryGetValue(searchTab, out var searchSession) && searchSession.Results.Contains(place))
        {
            searchSession.SelectedPlace = place;
            searchTab.Header = place.Name;
            return;
        }

        var results = new ObservableCollection<Place>([place]);
        if (_pinTab is null)
        {
            _pinTab = AddSearchTab(place.Name, results);
        }
        else
        {
            _searchSessions[_pinTab] = new SearchSession(place.Name, results);
            _changingSearchTabs = true;
            _displayedTab = _pinTab;
            SearchTabs.SelectedItem = _pinTab;
            _changingSearchTabs = false;
        }
        _searchSessions[_pinTab].SelectedPlace = place;
        _pinTab.Header = place.Name;
    }

    private async void SearchTabs_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_changingSearchTabs || SearchTabs.SelectedItem is not TabViewItem tab) return;
        if (tab == _displayedTab) return;
        _displayedTab = tab;
        if (tab == _routeTab)
        {
            ShowPane(DirectionsPane, "Directions");
            if (_route is not null) await FitAsync(_route.Points, MapAnimationKind.None);
            return;
        }
        if (!_searchSessions.TryGetValue(tab, out var session)) return;
        _searchRequests[SearchInput].Cancel();
        _selectionRequest.Cancel();
        SetText(SearchInput, session.Query);
        SearchResults.ItemsSource = session.Results;
        ClearMarkers(_resultsLayer);
        foreach (var place in session.Results) AddMarker(_resultsLayer, place, "PlacePin");
        _changingSearchTabs = true;
        try
        {
            if (session.SelectedPlace is not null)
                await SelectPlaceAsync(session.SelectedPlace, false);
            else
            {
                ShowSearchOverview();
            }
        }
        finally { _changingSearchTabs = false; }
        await FitAsync(session.SelectedPlace is Place selected ? [selected] : session.Results, MapAnimationKind.None);
    }

    private void SearchTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        _searchRequests[SearchInput].Cancel();
        _selectionRequest.Cancel();
        if (args.Tab == _routeTab)
        {
            _routeTab = null;
            ClearRoute();
        }
        if (args.Tab == _pinTab)
        {
            var session = _searchSessions[args.Tab];
            if (ReferenceEquals(SearchResults.ItemsSource, session.Results))
            {
                SearchResults.ItemsSource = null;
                ClearMarkers(_resultsLayer);
            }
            if (_selected is not null && session.Results.Contains(_selected))
            {
                _selected = null;
                ClearMarkers(_selectedLayer);
                PlaceDetails.Visibility = Visibility.Collapsed;
                SearchOverview.Visibility = Visibility.Visible;
            }
            _pinTab = null;
            UpdateSearchPaneLayout();
        }
        _searchSessions.Remove(args.Tab);
        sender.TabItems.Remove(args.Tab);
        if (sender.TabItems.Count == 0)
        {
            sender.Visibility = Visibility.Collapsed;
            SearchResults.ItemsSource = null;
            SetText(SearchInput, "");
            ClearMarkers(_resultsLayer);
            ShowSearchOverview();
        }
    }

    private void ShowSearchOverview()
    {
        _selected = null;
        ClearMarkers(_selectedLayer);
        PlaceDetails.Visibility = Visibility.Collapsed;
        SearchOverview.Visibility = Visibility.Visible;
        ShowPane(SearchPane, "Search");
    }

    private async void BackToResults_Click(object sender, RoutedEventArgs args)
    {
        if (SearchTabs.SelectedItem is TabViewItem tab && _searchSessions.TryGetValue(tab, out var session))
        {
            session.SelectedPlace = null;
            tab.Header = session.Query;
            SearchResults.ItemsSource = session.Results;
            SetText(SearchInput, session.Query);
            ShowSearchOverview();
            await FitAsync(session.Results);
        }
        else ShowSearchOverview();
    }

    private void ResultDirections_Click(object sender, RoutedEventArgs args)
    {
        if (((FrameworkElement)sender).Tag is Place place)
            SetRouteEndpoint(place, false);
    }
}
