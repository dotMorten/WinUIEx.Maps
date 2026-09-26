using Microsoft.UI;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Windows.Devices.Geolocation;
using WindowsMapsSample.Models;
using WindowsMapsSample.Services;
using WindowsMapsSample.ViewModels;
using WindowsMapsSample.Controls;
using WinUIEx.Maps;
using MapIcon = WinUIEx.Maps.MapIcon;
using MapElement = WinUIEx.Maps.MapElement;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;

namespace WindowsMapsSample;

public sealed partial class MainPage : Page
{
    private readonly Favorites _favorites;
    private readonly LatestRequest _tokenRequest = new(), _routeRequest = new(), _locationRequest = new(), _selectionRequest = new();
    private readonly Dictionary<AutoSuggestBox, LatestRequest> _searchRequests = [];
    private readonly MapElementsLayer _routeLayer = new(), _favoritesLayer = new(), _resultsLayer = new(), _locationLayer = new();
    private readonly MapElementsLayer _selectedLayer = new();
    private readonly AzureTrafficLayer _trafficLayer = new() { ShowIncidents = true };
    private readonly Dictionary<MapElement, Place> _places = [];
    private readonly Dictionary<string, ImageIcon> _pinSymbols = [];
    private readonly List<Place> _recentPlaces = [];
    private IMapsService? _service;
    private readonly Func<string, IMapsService> _serviceFactory;
    private readonly IMapServiceTokenStore _tokenStore;
    private readonly Window _window;
    private readonly MapStyle _initialStyle;
    private readonly ILocationService _locationService;
    private readonly PlaceBounds _startupBounds;
    private bool _startupViewPending;
    private Place? _selected, _origin, _destination, _location, _contextPlace;
    private Geopoint? _contextLocation;
    private MapRoute? _route;
    private AutoSuggestBox? _locationEndpoint;
    private bool _active, _settingText, _initialized;

    public MainPage() : this(token => new AzureMapsService(token), new MapServiceTokenStore(), App.Window, MapStyle.Road,
        favoritesStore: new FavoritesStore()) { }

    internal MainPage(Func<string, IMapsService> serviceFactory, IMapServiceTokenStore tokenStore, Window window, MapStyle initialStyle,
        ILocationService? locationService = null, string? startupRegion = null, IFavoritesStore? favoritesStore = null)
    {
        _serviceFactory = serviceFactory;
        _tokenStore = tokenStore;
        _window = window;
        _initialStyle = initialStyle;
        _locationService = locationService ?? new LocationService();
        _favorites = new Favorites(favoritesStore ?? new FavoritesStore());
        _startupBounds = StartupRegion.GetBounds(startupRegion ??
            Windows.System.UserProfile.GlobalizationPreferences.HomeGeographicRegion);
        InitializeComponent();
        _searchRequests.Add(SearchInput, new());
        _searchRequests.Add(OriginInput, new());
        _searchRequests.Add(DestinationInput, new());
        Map.Layers.Add(_routeLayer);
        Map.Layers.Add(_favoritesLayer);
        Map.Layers.Add(_resultsLayer);
        Map.Layers.Add(_locationLayer);
        Map.Layers.Add(_selectedLayer);
        InitializeInking();
        _resultsLayer.Tapped += Marker_Tapped;
        _favoritesLayer.Tapped += Marker_Tapped;
        _selectedLayer.Tapped += Marker_Tapped;
        _routeLayer.Tapped += Marker_Tapped;
        _locationLayer.Tapped += Marker_Tapped;
        _trafficLayer.IncidentTapped += TrafficIncident_Tapped;
        foreach (var layer in new[] { _resultsLayer, _favoritesLayer, _selectedLayer, _routeLayer, _locationLayer })
            layer.RightTapped += Marker_RightTapped;
        Map.Tapped += Map_Tapped;
        Map.Loaded += Map_StartupLayoutChanged;
        Map.SizeChanged += Map_StartupLayoutChanged;
        Map.RightTapped += Map_RightTapped;
        Map.AddHandler(UIElement.DoubleTappedEvent,
            new DoubleTappedEventHandler((_, _) => _selectionRequest.Cancel()), handledEventsToo: true);
        FavoritesList.ItemsSource = _favorites.Items;
        RestoreFavoriteMarkers();
        StylePicker.RegisterPropertyChangedCallback(MapStylePicker.SelectedStyleProperty, (_, _) => ApplyMapStyle());
        StylePicker.RegisterPropertyChangedCallback(MapStylePicker.IsTrafficEnabledProperty, (_, _) => ApplyTraffic());
        ActualThemeChanged += (_, _) => ApplyMapStyle();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _active = true;
        _window.Closed -= Window_Closed;
        _window.Closed += Window_Closed;
        if (_initialized) return;
        _initialized = true;
        string saved = _tokenStore.Load();
        if (!string.IsNullOrWhiteSpace(saved))
            await EnterShellAsync(saved, _serviceFactory(saved));
    }

    private async Task EnterShellAsync(string token, IMapsService service)
    {
        _service = service;
        _startupViewPending = true;
        Map.MapServiceToken = token;
        StylePicker.SelectedStyle = _initialStyle;
        Onboarding.Visibility = Visibility.Collapsed;
        Shell.Visibility = Visibility.Visible;
        TokenInput.Password = "";
        ShowPane(SearchPane, "Search");
        Shell.UpdateLayout();
        await InitializeStartupViewAsync();
    }

    private async void Map_StartupLayoutChanged(object sender, RoutedEventArgs args) =>
        await InitializeStartupViewAsync();

    private async Task InitializeStartupViewAsync()
    {
        if (!_startupViewPending || !_active || !Map.IsLoaded || Map.ActualWidth <= 0 || Map.ActualHeight <= 0)
            return;
        _startupViewPending = false;
        var bounds = new GeoboundingBox(
            new BasicGeoposition { Latitude = _startupBounds.North, Longitude = _startupBounds.West },
            new BasicGeoposition { Latitude = _startupBounds.South, Longitude = _startupBounds.East });
        var view = Map.TrySetViewBoundsAsync(bounds, new Thickness(24), MapAnimationKind.None);
        ApplyMapStyle();
        await view;
    }

    private void Token_Changed(object sender, RoutedEventArgs e)
    {
        if (ValidateButton is not null)
            ValidateButton.IsEnabled = !string.IsNullOrWhiteSpace(TokenInput.Password);
    }

    private async void ValidateToken_Click(object sender, RoutedEventArgs e)
    {
        string token = TokenInput.Password.Trim();
        if (token.Length == 0) return;
        var ticket = _tokenRequest.Begin();
        ValidateButton.IsEnabled = TokenInput.IsEnabled = false;
        TokenStatus.IsOpen = false;
        TokenProgress.Visibility = Visibility.Visible;
        TokenProgress.IsActive = true;
        try
        {
            var candidate = _serviceFactory(token);
            await candidate.ValidateAsync(ticket);
            if (!_active || !_tokenRequest.IsCurrent(ticket)) return;
            _tokenStore.Save(token);
            await EnterShellAsync(token, candidate);
        }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex))
        {
            if (_active)
            {
                TokenStatus.Message = ServiceErrors.Describe(ex);
                TokenStatus.IsOpen = true;
            }
        }
        finally
        {
            TokenInput.IsEnabled = true;
            ValidateButton.IsEnabled = !string.IsNullOrWhiteSpace(TokenInput.Password);
            TokenProgress.IsActive = false;
            TokenProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_settingText || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || _service is null) return;
        // TextChanged can be queued until after a selected endpoint's text has been assigned.
        if ((sender == OriginInput && sender.Text == _origin?.Name) ||
            (sender == DestinationInput && sender.Text == _destination?.Name)) return;
        if (_locationEndpoint == sender)
        {
            if (sender.Text == "My location") return;
            _locationRequest.Cancel();
            _locationEndpoint = null;
        }
        if (sender == OriginInput) _origin = null;
        if (sender == DestinationInput) _destination = null;
        if (sender != SearchInput) ClearRoute();
        else
        {
            DeselectSearchTab();
            _selectionRequest.Cancel();
            _selected = null;
            ClearMarkers(_selectedLayer);
            PlaceDetails.Visibility = Visibility.Collapsed;
            SearchResults.ItemsSource = null;
            ClearMarkers(_resultsLayer);
            UpdateSearchPaneLayout();
        }
        await SearchAsync(sender, true);
    }

    private async Task SearchAsync(AutoSuggestBox sender, bool debounce)
    {
        if (_service is null) return;
        var request = _searchRequests[sender];
        var ticket = request.Begin();
        string query = sender.Text.Trim();
        if (sender == SearchInput)
        {
            sender.ItemsSource = null;
            if (!debounce) sender.IsSuggestionListOpen = false;
        }
        else SetEndpointSuggestions(sender, []);
        if (query.Length < 2)
        {
            request.Cancel();
            if (sender != SearchInput) SetEndpointSuggestions(sender, _recentPlaces);
            if (!debounce) ShowStatus("Enter at least two characters to search.");
            return;
        }
        try
        {
            if (debounce) await Task.Delay(350, ticket);
            var results = await _service.SearchAsync(query, ticket, GetSearchArea());
            if (!_active || !request.IsCurrent(ticket)) return;
            var items = new ObservableCollection<Place>(results);
            if (sender == SearchInput)
            {
                if (debounce) sender.ItemsSource = items;
                else sender.IsSuggestionListOpen = false;
            }
            else SetEndpointSuggestions(sender, results);
            if (sender == SearchInput)
            {
                if (!debounce) AddSearchTab(query, items);
                SearchResults.ItemsSource = items;
                ShowSearchOverview();
                ClearMarkers(_resultsLayer);
                foreach (var place in results) AddMarker(_resultsLayer, place, "PlacePin");
                if (results.Count > 0 && !debounce) await FitAsync(results);
            }
            if (_active && request.IsCurrent(ticket))
            {
                if (results.Count == 0)
                    ShowStatus("No places found. Try a more specific address.", false);
                else
                    Status.IsOpen = false;
            }
        }
        catch (OperationCanceledException) when (!request.Owns(ticket)) { }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex))
        {
            if (_active && request.Owns(ticket)) ShowStatus(ServiceErrors.Describe(ex));
        }
    }

    private async void Search_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        sender.IsSuggestionListOpen = false;
        if (args.ChosenSuggestion is EndpointSuggestion suggestion) await ApplyEndpointSuggestionAsync(sender, suggestion);
        else if (args.ChosenSuggestion is Place place) await ApplySuggestionAsync(sender, place);
        else await SearchAsync(sender, false);
    }

    private void Suggestion_Chosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is EndpointSuggestion suggestion)
            SetText(sender, suggestion.Label);
        else if (args.SelectedItem is Place place)
            SetText(sender, place.Name);
    }

    private async Task ApplySuggestionAsync(AutoSuggestBox sender, Place place)
    {
        RememberRecentPlace(place);
        _searchRequests[sender].Cancel();
        SetText(sender, place.Name);
        sender.IsSuggestionListOpen = false;
        if (sender == OriginInput) { _origin = place; ClearRoute(); }
        else if (sender == DestinationInput) { _destination = place; ClearRoute(); }
        else
        {
            sender.ItemsSource = null;
            AddSearchTab(place.Name, new ObservableCollection<Place>([place]));
            await SelectPlaceAsync(place);
        }
    }

    private void Endpoint_GotFocus(object sender, RoutedEventArgs args)
    {
        var input = (AutoSuggestBox)sender;
        if (input.ItemsSource is null) SetEndpointSuggestions(input, _recentPlaces);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!input.IsLoaded) return;
            var focused = FocusManager.GetFocusedElement(input.XamlRoot) as DependencyObject;
            while (focused is not null && focused != input) focused = VisualTreeHelper.GetParent(focused);
            if (focused == input) input.IsSuggestionListOpen = true;
        });
    }

    private void SetEndpointSuggestions(AutoSuggestBox input, IEnumerable<Place> places)
    {
        var suggestions = new ObservableCollection<EndpointSuggestion> { new("My location", null) };
        var coordinates = new HashSet<(double Latitude, double Longitude)>();
        string query = input.Text.Trim();

        void AddMatches(IEnumerable<Place> candidates, bool requireMatch)
        {
            foreach (Place place in candidates)
            {
                if (requireMatch && !MatchesEndpointQuery(place, query)) continue;
                if (coordinates.Add((place.Latitude, place.Longitude)))
                    suggestions.Add(new EndpointSuggestion(place.Name, place));
            }
        }

        AddMatches(_favorites.Items, requireMatch: true);
        AddMatches(_recentPlaces, requireMatch: true);
        AddMatches(places, requireMatch: false);
        input.ItemsSource = suggestions;
    }

    private static bool MatchesEndpointQuery(Place place, string query) =>
        query.Length == 0 ||
        place.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        place.Address.Contains(query, StringComparison.OrdinalIgnoreCase);

    private async Task ApplyEndpointSuggestionAsync(AutoSuggestBox input, EndpointSuggestion suggestion)
    {
        _searchRequests[input].Cancel();
        input.IsSuggestionListOpen = false;
        if (suggestion.Place is Place place) await ApplySuggestionAsync(input, place);
        else await LocateAsync(input);
    }

    private void RememberRecentPlace(Place place)
    {
        _recentPlaces.RemoveAll(item => item.Latitude == place.Latitude && item.Longitude == place.Longitude);
        _recentPlaces.Insert(0, place);
        if (_recentPlaces.Count > 10) _recentPlaces.RemoveAt(10);
    }

    private void SetText(AutoSuggestBox input, string value)
    {
        if (input.Text == value) return;
        _settingText = true;
        input.Text = value;
        _settingText = false;
    }

    private async void Place_Clicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is Place place)
            await SelectPlaceAsync(place);
    }

    private async void Marker_Tapped(object? sender, MapElementTappedEventArgs args)
    {
        if (_places.TryGetValue(args.MapElement, out var place))
        {
            args.Handled = true;
            await SelectPlaceAsync(place);
        }
    }

    private void Marker_RightTapped(object? sender, MapElementRightTappedEventArgs args)
    {
        if (_places.TryGetValue(args.MapElement, out var place))
        {
            args.Handled = true;
            ShowPinMenu(place, Map, args.GetPosition(Map));
        }
    }

    private void TrafficIncident_Tapped(object? sender, AzureTrafficIncidentEventArgs args)
    {
        if (!_active || StylePicker.IsInkEnabled) return;
        args.Handled = true;
        _selectionRequest.Cancel();
        UpdateTrafficIncidentDetails(args);
        var options = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = args.Position };
        ((Flyout)Resources["TrafficIncidentFlyout"]).ShowAt(Map, options);
    }

    internal void UpdateTrafficIncidentDetails(AzureTrafficIncidentEventArgs args)
    {
        TrafficIncidentContent.MaxWidth = Math.Max(0, Map.ActualWidth - 48);
        TrafficIncidentTitle.Text = args.IncidentType ??
            (args.Category is { } category ? IncidentCategoryName(category) : "Traffic incident");
        SetDetail(TrafficIncidentDelay, args.Delay is { } delay ? $"{delay.TotalMinutes:0.#} min delay" : null);
        SetDetail(TrafficIncidentDescription, args.Description);
        SetDetail(TrafficIncidentStart, FormatIncidentTime("Start", args.StartTime));
        SetDetail(TrafficIncidentEnd, FormatIncidentTime("Est. end", args.EndTime));
        TrafficIncidentTimes.Visibility = args.StartTime is null && args.EndTime is null
            ? Visibility.Collapsed : Visibility.Visible;

        static void SetDetail(TextBlock element, string? text)
        {
            element.Text = text ?? string.Empty;
            element.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        static string? FormatIncidentTime(string label, DateTimeOffset? time) =>
            time is { } value ? $"{label}: {value.ToLocalTime():ddd, MMM d} - {value.ToLocalTime():t}" : null;
    }

    private static string IncidentCategoryName(int category) => category switch
    {
        0 => "Unknown", 1 => "Accident", 2 => "Fog", 3 => "Dangerous conditions",
        4 => "Rain", 5 => "Ice", 6 => "Congestion", 7 => "Lane closed",
        8 => "Road closed", 9 => "Road works", 10 => "Wind", 11 => "Flooding",
        12 => "Detour", 13 => "Multiple incidents", 14 => "Broken-down vehicle",
        _ => "Traffic incident",
    };

    private void Map_RightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (StylePicker.IsInkEnabled || args.Handled || _service is null ||
            !Map.TryGetLocationFromOffset(args.GetPosition(Map), out var location)) return;
        args.Handled = true;
        _selectionRequest.Cancel();
        _contextLocation = location;
        var position = location.Position;
        var menu = (MenuFlyout)Resources["MapContextMenu"];
        ((MenuFlyoutItem)menu.Items[0]).Text = FormattableString.Invariant(
            $"Latitude: {position.Latitude:F5}, Longitude: {position.Longitude:F5}");
        menu.ShowAt(Map, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = args.GetPosition(Map) });
    }

    private void MapContextMenu_Closed(object sender, object args) => _contextLocation = null;

    private async void DropPin_Click(object sender, RoutedEventArgs args)
    {
        if (_contextLocation is Geopoint location)
            await DropPinAsync(location);
    }

    private void ShowPinMenu(Place place, FrameworkElement target, Windows.Foundation.Point position)
    {
        _selectionRequest.Cancel();
        _contextPlace = place;
        var menu = (MenuFlyout)Resources["PinContextMenu"];
        bool saved = _favorites.Contains(place);
        menu.Items[2].Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        menu.Items[3].Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        menu.ShowAt(target, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = position });
    }

    private void PinContextMenu_Closed(object sender, object args) => _contextPlace = null;
    private void PinDirectionsTo_Click(object sender, RoutedEventArgs args)
    {
        if (_contextPlace is not null) SetRouteEndpoint(_contextPlace, false);
    }
    private void PinSave_Click(object sender, RoutedEventArgs args)
    {
        if (_contextPlace is not null && !_favorites.Contains(_contextPlace))
            ToggleFavorite(_contextPlace);
    }
    private void PinDelete_Click(object sender, RoutedEventArgs args)
    {
        if (_contextPlace is not null) DeleteSavedPlace(_contextPlace);
    }

    private void DeleteSavedPlace(Place place)
    {
        if (_favorites.Contains(place))
        {
            ToggleFavorite(place);
            if (_selected == place)
            {
                ShowSearchOverview();
            }
        }
    }

    private async void Map_Tapped(object sender, TappedRoutedEventArgs args)
    {
        if (StylePicker.IsInkEnabled) return;
        if (args.Handled || _service is null || !Map.TryGetLocationFromOffset(args.GetPosition(Map), out var location)) return;
        args.Handled = true;
        await DropPinAsync(location, deferForDoubleTap: true);
    }

    private async Task DropPinAsync(Geopoint location, bool deferForDoubleTap = false)
    {
        if (!_active || _service is null) return;
        var ticket = _selectionRequest.Begin();
        try
        {
            // Tapped precedes DoubleTapped; do not publish a pin until the gesture is unambiguous.
            if (deferForDoubleTap)
                await Task.Delay(TimeSpan.FromMilliseconds(GetDoubleClickTime()), ticket);
            if (!_active || !_selectionRequest.IsCurrent(ticket)) return;
            _searchRequests[SearchInput].Cancel();
            SearchInput.ItemsSource = SearchResults.ItemsSource = null;
            ClearMarkers(_resultsLayer);
            var position = location.Position;
            var pin = new Place("Dropped pin", FormattableString.Invariant($"{position.Latitude:F5}, {position.Longitude:F5}"),
                position.Latitude, position.Longitude);
            await SelectPlaceAsync(pin, false);
            ticket = _selectionRequest.Begin();
            var place = await _service.ReverseGeocodeAsync(pin.Latitude, pin.Longitude, ticket);
            if (!_active || !_selectionRequest.IsCurrent(ticket)) return;
            if (place is not null) await SelectPlaceAsync(place, false);
            else ShowStatus("No street address was found. You can still save this pin or get directions to it.", false);
        }
        catch (OperationCanceledException) when (!_selectionRequest.Owns(ticket)) { }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex))
        {
            if (_active && _selectionRequest.Owns(ticket))
                ShowStatus($"The pin is selected, but its address could not be loaded. {ServiceErrors.Describe(ex)}");
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    private async Task SelectPlaceAsync(Place place, bool adjustView = true)
    {
        _selectionRequest.Cancel();
        RememberRecentPlace(place);
        RememberSelectedPlace(place);
        _selected = place;
        PlaceName.Text = place.Name;
        PlaceAddress.Text = place.Address;
        UpdateFavoriteAction();
        UpdateSelectedMarker();
        PlaceDetails.Visibility = Visibility.Visible;
        SearchOverview.Visibility = Visibility.Collapsed;
        ShowPane(SearchPane, "Search");
        if (adjustView) await FitAsync([place]);
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not Place place) return;
        if (_favorites.Contains(place)) DeleteSavedPlace(place);
        else ToggleFavorite(place);
    }

    private void ToggleFavorite(Place place)
    {
        _selectionRequest.Cancel();
        _favorites.Toggle(place);
        UpdateFavoriteAction();
        UpdateSelectedMarker();
        RestoreFavoriteMarkers();
    }

    private async void EditFavoriteName_Click(object sender, RoutedEventArgs args)
    {
        if (((FrameworkElement)sender).Tag is not Place favorite) return;
        await RenameFavoriteAsync(favorite);
    }

    private async void EditSelectedFavoriteName_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is Place favorite && _favorites.Contains(favorite))
            await RenameFavoriteAsync(favorite);
    }

    private async Task RenameFavoriteAsync(Place favorite)
    {
        var name = new TextBox
        {
            Header = "Name",
            Text = favorite.Name,
            TextWrapping = TextWrapping.Wrap,
        };
        var dialog = new ContentDialog
        {
            Title = "Rename saved place",
            Content = name,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!_favorites.Rename(favorite, name.Text))
        {
            ShowStatus("A saved place name cannot be empty.");
            return;
        }

        Place renamed = _favorites.Items.First(item =>
            item.Latitude == favorite.Latitude && item.Longitude == favorite.Longitude);
        if (_selected is not null && _favorites.Contains(_selected))
        {
            _selected = renamed;
            PlaceName.Text = renamed.Name;
            PlaceAddress.Text = renamed.Address;
            UpdateSelectedMarker();
        }
        RestoreFavoriteMarkers();
        UpdateFavoriteAction();
    }

    private void RestoreFavoriteMarkers()
    {
        ClearMarkers(_favoritesLayer);
        foreach (Place favorite in _favorites.Items) AddMarker(_favoritesLayer, favorite, "SavedPin");
    }

    private void UpdateFavoriteAction()
    {
        bool saved = _selected is not null && _favorites.Contains(_selected);
        FavoriteLabel.Text = saved ? "Delete saved pin" : "Save place";
        FavoriteGlyph.Glyph = saved ? "\uE74D" : "\uE734";
        AutomationProperties.SetName(FavoriteButton, FavoriteLabel.Text);
        EditSelectedFavoriteNameButton.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSelectedMarker()
    {
        string symbolKey = _selected is not null && _favorites.Contains(_selected) ? "SavedPin" : "PlacePin";
        if (_selected is not null && _selectedLayer.MapElements.FirstOrDefault() is MapIcon marker &&
            _pinSymbols.TryGetValue(symbolKey, out var symbol) && ReferenceEquals(marker.IconElement, symbol))
        {
            marker.Location = ToGeopoint(_selected);
            _places[marker] = _selected;
            return;
        }
        ClearMarkers(_selectedLayer);
        if (_selected is not null)
            AddMarker(_selectedLayer, _selected, symbolKey);
    }

    private void DirectionsToPlace_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) SetRouteEndpoint(_selected, false);
    }

    private void SetRouteEndpoint(Place place, bool origin)
    {
        var input = origin ? OriginInput : DestinationInput;
        _searchRequests[input].Cancel();
        input.ItemsSource = null;
        input.IsSuggestionListOpen = false;
        if (origin) _origin = place;
        else _destination = place;
        SetText(input, place.Name);
        ClearRoute();
        ShowPane(DirectionsPane, "Directions");
    }

    private async void Route_Click(object sender, RoutedEventArgs e)
    {
        ClearRoute();
        _searchRequests[OriginInput].Cancel();
        _searchRequests[DestinationInput].Cancel();
        OriginInput.IsSuggestionListOpen = DestinationInput.IsSuggestionListOpen = false;
        if (_service is null || string.IsNullOrWhiteSpace(OriginInput.Text) || string.IsNullOrWhiteSpace(DestinationInput.Text))
        {
            ShowStatus("Choose both endpoints from the suggestions, or use your current location.");
            return;
        }
        var ticket = _routeRequest.Begin();
        Status.IsOpen = false;
        SetRouteBusy(true);
        try
        {
            var service = _service;
            var origin = _origin;
            var destination = _destination;
            if (origin is null)
            {
                var candidates = await service.SearchAsync(OriginInput.Text, ticket, GetSearchArea());
                if (candidates.Count == 1) origin = candidates[0];
            }
            if (destination is null)
            {
                var candidates = await service.SearchAsync(DestinationInput.Text, ticket, GetSearchArea());
                if (candidates.Count == 1) destination = candidates[0];
            }
            if (!_active || !_routeRequest.IsCurrent(ticket)) return;
            if (origin is null || destination is null)
            {
                ShowStatus("An endpoint is unknown or ambiguous. Search and choose a specific suggestion for each endpoint.");
                return;
            }
            _origin = origin;
            _destination = destination;
            SetText(OriginInput, origin.Name);
            SetText(DestinationInput, destination.Name);
            var route = await service.RouteAsync(origin, destination, TravelModePicker.SelectedItem == WalkingMode, ticket);
            if (!_active || !_routeRequest.IsCurrent(ticket)) return;
            if (route is null || route.Points.Count < 2)
            {
                ShowStatus("No route was found for these endpoints and travel mode.");
                return;
            }
            var path = CreateRoutePath(route.Points);
            _route = route;
            _routeLayer.MapElements.Add(new MapPolyline
            {
                Path = path,
                StrokeColor = Colors.White, StrokeThickness = 9,
            });
            _routeLayer.MapElements.Add(new MapPolyline
            {
                Path = path,
                StrokeColor = Colors.DodgerBlue, StrokeThickness = 5,
            });
            AddMarker(_routeLayer, _origin, "StartPin", true);
            AddMarker(_routeLayer, _destination, "EndPin");
            RouteSummary.Text = route.Summary;
            RouteOriginLabel.Text = origin.Name;
            RouteCard.Visibility = Visibility.Visible;
            RouteEditor.Visibility = Visibility.Collapsed;
            Itinerary.ItemsSource = new ObservableCollection<RouteStep>(route.Steps);
            SelectRouteTab();
            if (route.Steps.Count == 0) ShowStatus("This route has no written instructions.", false);
            await FitAsync(route.Points);
        }
        catch (OperationCanceledException) when (!_routeRequest.Owns(ticket)) { }
        catch (System.Runtime.InteropServices.COMException)
        {
            if (_active && _routeRequest.Owns(ticket))
                ShowStatus("The route could not be displayed. Try choosing the endpoints again.");
        }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex))
        {
            if (_active && _routeRequest.Owns(ticket)) ShowStatus(ServiceErrors.Describe(ex));
        }
        finally
        {
            if (_routeRequest.Owns(ticket)) SetRouteBusy(false);
        }
    }

    private void SetRouteBusy(bool busy)
    {
        if (RouteProgress is not null)
        {
            RouteProgress.IsIndeterminate = busy;
            RouteProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }
        if (GetDirectionsButton is not null) GetDirectionsButton.IsEnabled = !busy;
    }

    private void ClearRoute()
    {
        _routeRequest.Cancel();
        _route = null;
        SetRouteBusy(false);
        ClearMarkers(_routeLayer);
        if (RouteSummary is not null) RouteSummary.Text = "";
        if (RouteCard is not null) RouteCard.Visibility = Visibility.Collapsed;
        if (RouteEditor is not null) RouteEditor.Visibility = Visibility.Visible;
        if (Itinerary is not null) Itinerary.ItemsSource = null;
    }

    private void EditDirections_Click(object sender, RoutedEventArgs e)
    {
        RouteEditor.Visibility = Visibility.Visible;
        RouteCard.Visibility = Visibility.Collapsed;
    }

    private void ClearRoute_Click(object sender, RoutedEventArgs e)
    {
        _origin = _destination = null;
        _searchRequests[OriginInput].Cancel();
        _searchRequests[DestinationInput].Cancel();
        SetText(OriginInput, "");
        SetText(DestinationInput, "");
        ClearRoute();
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        _searchRequests[OriginInput].Cancel();
        _searchRequests[DestinationInput].Cancel();
        (_origin, _destination) = (_destination, _origin);
        string originText = OriginInput.Text;
        SetText(OriginInput, DestinationInput.Text);
        SetText(DestinationInput, originText);
        ClearRoute();
    }

    private void TravelMode_Changed(SelectorBar sender, SelectorBarSelectionChangedEventArgs e) => ClearRoute();

    private async void Step_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is RouteStep step)
            await FitAsync([new Place(step.Instruction, "", step.Latitude, step.Longitude)]);
    }

    private async Task LocateAsync(AutoSuggestBox? endpoint = null)
    {
        var ticket = _locationRequest.Begin();
        _locationEndpoint = endpoint;
        Status.IsOpen = false;
        try
        {
            var location = await _locationService.LocateAsync(ticket);
            if (!_active || !_locationRequest.IsCurrent(ticket)) return;
            _location = location;
            if (_locationLayer.MapElements.FirstOrDefault() is MapIcon marker)
            {
                marker.Location = ToGeopoint(location);
                _places[marker] = location;
            }
            else
            {
                AddMarker(_locationLayer, location, "LocationDot", true);
            }
            if (endpoint is not null)
            {
                if (endpoint == OriginInput) _origin = _location;
                else _destination = _location;
                _searchRequests[endpoint].Cancel();
                SetText(endpoint, _location.Name);
                endpoint.IsSuggestionListOpen = false;
                ClearRoute();
            }
            await FitAsync([_location]);
        }
        catch (OperationCanceledException) when (!_locationRequest.Owns(ticket)) { }
        catch (UnauthorizedAccessException)
        {
            if (_active && _locationRequest.Owns(ticket))
                ShowStatus("Location access is disabled. Allow this app to use location in Windows Settings.");
        }
        catch (Exception ex) when (ServiceErrors.IsExpected(ex) || ex is System.Runtime.InteropServices.COMException)
        {
            if (_active) ShowStatus("Location is unavailable. Check Windows location permissions and try again.");
        }
        finally
        {
            if (_locationRequest.Owns(ticket)) _locationEndpoint = null;
        }
    }

    private async void Navigation_LocateRequested(object sender, RoutedEventArgs e) => await LocateAsync();
    private void Navigation_ZoomInRequested(object sender, RoutedEventArgs e) => Map.ZoomLevel += 1;
    private void Navigation_ZoomOutRequested(object sender, RoutedEventArgs e) => Map.ZoomLevel -= 1;
    private void Navigation_NorthUpRequested(object sender, RoutedEventArgs e) => Map.Heading = 0;
    private void Navigation_TiltRequested(object sender, RoutedEventArgs e) => Map.Pitch = Map.Pitch > 0 ? 0 : 45;

    private void AddMarker(MapElementsLayer layer, Place place, string symbolKey, bool centered = false)
    {
        if (!_pinSymbols.TryGetValue(symbolKey, out var symbol))
        {
            string asset = symbolKey == "LocationDot" ? "Location" : symbolKey[..^3];
            // Keep rasterized elements unowned: ResourceDictionary-owned UIElements cannot be reparented.
            symbol = new ImageIcon
            {
                Width = 32,
                Height = centered ? 32 : 42,
                Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Pins/{asset}.svg")),
            };
            _pinSymbols.Add(symbolKey, symbol);
        }
        var marker = new MapIcon(symbol, ToGeopoint(place))
        {
            NormalizedAnchorPoint = new(0.5, centered ? 0.5 : 40d / 42),
        };
        layer.MapElements.Add(marker);
        _places.Add(marker, place);
    }

    private void ClearMarkers(MapElementsLayer layer)
    {
        foreach (var element in layer.MapElements) _places.Remove(element);
        layer.MapElements.Clear();
    }

    private SearchArea? GetSearchArea()
    {
        if (!Map.TryGetLocationFromOffset(new(Map.ActualWidth / 2, Map.ActualHeight / 2), out var center))
            return null;
        var position = center.Position;
        var area = new SearchArea(position.Latitude, position.Longitude, 0);
        double radius = 1000;
        foreach (var offset in new Windows.Foundation.Point[]
        {
            new(0, 0), new(Map.ActualWidth, 0),
            new(0, Map.ActualHeight), new(Map.ActualWidth, Map.ActualHeight),
        })
        {
            if (!Map.TryGetLocationFromOffset(offset, out var corner)) continue;
            var point = corner.Position;
            radius = Math.Max(radius, area.DistanceTo(point.Latitude, point.Longitude) * 1.25);
        }
        return new SearchArea(position.Latitude, position.Longitude, (int)Math.Clamp(Math.Ceiling(radius), 1000, 50000));
    }

    private async Task FitAsync(IReadOnlyList<Place> places, MapAnimationKind animation = MapAnimationKind.Bow)
    {
        if (places.Count == 0) return;
        StylePicker.IsInkEnabled = false;
        // Fit with padding so the selected place or route is not hidden behind the pane.
        var margin = new Thickness(Pane.Visibility == Visibility.Visible ? 400 : 48, 80, 100, 80);
        if (places.Count == 1 && places[0].Bounds is PlaceBounds region &&
            (region.North - region.South >= 0.02 || (region.East - region.West + 360) % 360 >= 0.02))
        {
            // Preserve the service's west/east ordering, including countries spanning the date line.
            var regionBounds = new GeoboundingBox(
                new BasicGeoposition { Latitude = region.North, Longitude = region.West },
                new BasicGeoposition { Latitude = region.South, Longitude = region.East });
            await Map.TrySetViewBoundsAsync(regionBounds, margin, animation);
            return;
        }
        var positions = new List<BasicGeoposition>();
        foreach (var place in places)
        {
            if (place.Bounds is PlaceBounds extent)
            {
                positions.Add(new() { Latitude = extent.North, Longitude = extent.West });
                positions.Add(new() { Latitude = extent.South, Longitude = extent.East });
            }
            // A point has no extent: show its neighborhood instead of fitting to maximum zoom.
            const double latitudeRadius = 1000 / 111320d;
            double longitudeRadius = latitudeRadius / Math.Max(0.01, Math.Cos(place.Latitude * Math.PI / 180));
            positions.Add(new()
            {
                Latitude = Math.Clamp(place.Latitude + latitudeRadius, -85, 85),
                Longitude = (place.Longitude - longitudeRadius + 540) % 360 - 180,
            });
            positions.Add(new()
            {
                Latitude = Math.Clamp(place.Latitude - latitudeRadius, -85, 85),
                Longitude = (place.Longitude + longitudeRadius + 540) % 360 - 180,
            });
        }
        var bounds = GeoboundingBox.TryCompute(positions);
        if (bounds is not null)
            await Map.TrySetViewBoundsAsync(bounds, margin, animation);
    }

    private static BasicGeoposition ToPosition(Place place) => new() { Latitude = place.Latitude, Longitude = place.Longitude };
    private static Geopoint ToGeopoint(Place place) => new(ToPosition(place));

    private static Geopath CreateRoutePath(IReadOnlyList<Place> routePoints)
    {
        var positions = new List<BasicGeoposition>(routePoints.Count);
        foreach (Place point in routePoints)
        {
            if (!double.IsFinite(point.Latitude) ||
                !double.IsFinite(point.Longitude) ||
                point.Latitude is < -90 or > 90 ||
                point.Longitude is < -180 or > 180)
            {
                throw new InvalidDataException("The route contains an invalid geometry point.");
            }

            positions.Add(ToPosition(point));
        }

        if (positions.Count < 2)
            throw new InvalidDataException("The route contains too few geometry points.");

        try
        {
            return new Geopath(positions);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The route geometry could not be created.", exception);
        }
    }

    private void ShowStatus(string text, bool error = true)
    {
        Status.Message = text;
        Status.Severity = error ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        Status.IsOpen = true;
        Pane.Visibility = Visibility.Visible;
    }

    private void ShowPane(FrameworkElement panel, string title)
    {
        _selectionRequest.Cancel();
        foreach (var item in new FrameworkElement[] { SearchPane, DirectionsPane, FavoritesPane, SettingsPane })
            item.Visibility = item == panel ? Visibility.Visible : Visibility.Collapsed;
        PaneTitle.Text = title;
        Pane.Visibility = Visibility.Visible;
        Status.IsOpen = false;
        UpdateSearchPaneLayout();
    }

    private void UpdateSearchPaneLayout()
    {
        bool compact = SearchPane.Visibility == Visibility.Visible &&
            PlaceDetails.Visibility != Visibility.Visible && SearchResults.Items.Count == 0;
        Pane.VerticalAlignment = compact ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        PaneHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PaneContent.Padding = new Thickness(compact ? 0 : 16);
        Status.Margin = compact ? new Thickness(0) : new Thickness(0, 8, 0, 8);
        SearchResults.Visibility = SearchResults.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchPane_Click(object sender, RoutedEventArgs e)
    {
        DeselectSearchTab();
        SetText(SearchInput, "");
        SearchResults.ItemsSource = null;
        ClearMarkers(_resultsLayer);
        ShowSearchOverview();
    }
    private void DirectionsPane_Click(object sender, RoutedEventArgs e) => ShowPane(DirectionsPane, "Directions");
    private void FavoritesPane_Click(object sender, RoutedEventArgs e) => ShowPane(FavoritesPane, "Saved places");
    private void SettingsPane_Click(object sender, RoutedEventArgs e) => ShowPane(SettingsPane, "Settings");
    private void ClosePane_Click(object sender, RoutedEventArgs e)
    {
        Pane.Visibility = Visibility.Collapsed;
        _selectionRequest.Cancel();
    }

    private void ReplaceKey_Click(object sender, RoutedEventArgs e) => LockShell(false);
    private void RemoveKey_Click(object sender, RoutedEventArgs e) => LockShell(true);
    private void LockShell(bool remove)
    {
        StylePicker.IsInkEnabled = false;
        if (remove)
        {
            try { _tokenStore.Save(""); }
            catch (Exception ex) when (ServiceErrors.IsExpected(ex)) { ShowStatus(ServiceErrors.Describe(ex)); return; }
        }
        CancelRequests();
        _service = null;
        Map.MapStyle = MapStyle.Blank;
        Map.MapServiceToken = "";
        ClearRoute();
        Shell.Visibility = Visibility.Collapsed;
        Onboarding.Visibility = Visibility.Visible;
        TokenStatus.IsOpen = false;
        TokenInput.Focus(FocusState.Programmatic);
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_window.Content is FrameworkElement root && ((ComboBox)sender).SelectedIndex >= 0)
            root.RequestedTheme = (ElementTheme)((ComboBox)sender).SelectedIndex;
    }

    private void ApplyMapStyle()
    {
        if (_service is null || _startupViewPending) return;
        StylePicker.IsInkEnabled = false;
        Map.MapStyle = StylePicker.SelectedStyle is MapStyle.Road or MapStyle.Night
            ? ActualTheme == ElementTheme.Dark ? MapStyle.Night : MapStyle.Road
            : StylePicker.SelectedStyle;
        ApplyTraffic();
    }

    private void ApplyTraffic()
    {
        _trafficLayer.FlowStyle = ActualTheme == ElementTheme.Dark
            ? TrafficFlowStyle.RelativeDark : TrafficFlowStyle.Relative;
        if (StylePicker.IsTrafficEnabled)
        {
            if (!Map.Layers.Contains(_trafficLayer))
                Map.Layers.Insert(0, _trafficLayer);
        }
        else
        {
            ((Flyout)Resources["TrafficIncidentFlyout"]).Hide();
            Map.Layers.Remove(_trafficLayer);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _window.Close();
    private void CancelRequests()
    {
        ((Flyout)Resources["TrafficIncidentFlyout"]).Hide();
        ((MenuFlyout)Resources["MapContextMenu"]).Hide();
        _contextLocation = null;
        ((MenuFlyout)Resources["PinContextMenu"]).Hide();
        _contextPlace = null;
        _tokenRequest.Cancel();
        _routeRequest.Cancel();
        SetRouteBusy(false);
        _locationRequest.Cancel();
        _locationEndpoint = null;
        _selectionRequest.Cancel();
        foreach (var request in _searchRequests.Values) request.Cancel();
    }
    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _active = false;
        CancelRequests();
        _window.Closed -= Window_Closed;
    }
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _active = false;
        CancelRequests();
        _window.Closed -= Window_Closed;
    }
}
