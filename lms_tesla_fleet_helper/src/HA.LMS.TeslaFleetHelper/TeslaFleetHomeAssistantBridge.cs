using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;

sealed class TeslaFleetTokenCoordinator(TeslaFleetOAuthClient oauthClient)
{
    public async Task<TeslaAccessTokenResult> EnsureUsableAsync(
        TeslaFleetState state,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(state.AccessToken) &&
            state.TokenExpiresUtc.HasValue &&
            state.TokenExpiresUtc.Value > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return new TeslaAccessTokenResult(
                state,
                false,
                [$"Access token is valid until {state.TokenExpiresUtc.Value:O}."]);
        }

        return await RefreshAsync(state, cancellationToken);
    }

    public async Task<TeslaAccessTokenResult> RefreshAsync(
        TeslaFleetState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.RefreshToken))
        {
            throw new InvalidOperationException("Complete Tesla OAuth before refreshing the access token.");
        }

        if (string.IsNullOrWhiteSpace(state.TeslaClientId) ||
            string.IsNullOrWhiteSpace(state.TeslaClientSecret))
        {
            throw new InvalidOperationException("Tesla client ID and client secret are required to refresh the OAuth token.");
        }

        var token = await oauthClient.RefreshAccessTokenAsync(
            state.TeslaClientId,
            state.TeslaClientSecret,
            state.RefreshToken,
            state.FleetApiAudience,
            state.TeslaScopes,
            cancellationToken);
        var expiresUtc = token.ExpiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn)
            : (DateTimeOffset?)null;
        var fleetApiAudience = TeslaFleetDefaults.ResolveFleetApiAudience(state.FleetApiAudience, token.AccessToken);
        var updated = state with
        {
            FleetApiAudience = fleetApiAudience,
            AccessToken = token.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? state.RefreshToken : token.RefreshToken,
            TokenType = token.TokenType,
            TokenExpiresUtc = expiresUtc,
            LastTokenRefreshUtc = DateTimeOffset.UtcNow
        };
        var checks = new List<string>
        {
            "Tesla refresh token grant completed.",
            $"Fleet API base URL: {fleetApiAudience}.",
            expiresUtc.HasValue ? $"Access token expires at {expiresUtc.Value:O}." : "Tesla did not return an access token expiry."
        };
        return new TeslaAccessTokenResult(updated, true, checks);
    }
}

sealed class TeslaFleetDataClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<TeslaFleetSnapshot> FetchSnapshotAsync(
        TeslaFleetState state,
        CancellationToken cancellationToken)
    {
        var audience = TeslaFleetDefaults.NormalizeHttpUrl(state.FleetApiAudience, TeslaFleetDefaults.DefaultFleetApiAudience);
        var checks = new List<string>();
        JsonElement? me = await TryGetResponseAsync(audience, "/api/1/users/me", state.AccessToken, checks, cancellationToken);
        JsonElement? region = await TryGetResponseAsync(audience, "/api/1/users/region", state.AccessToken, checks, cancellationToken);
        JsonElement? products = await TryGetResponseAsync(audience, "/api/1/products", state.AccessToken, checks, cancellationToken);

        var vehicles = ReadVehicles(products).Concat(await ReadVehiclesAsync(audience, state.AccessToken, checks, cancellationToken))
            .GroupBy(vehicle => vehicle.Vin, StringComparer.OrdinalIgnoreCase)
            .Select(group => MergeVehicle(group.ToArray()))
            .OrderBy(vehicle => vehicle.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fleetStatus = vehicles.Count == 0
            ? null
            : await TryPostResponseAsync(
                audience,
                "/api/1/vehicles/fleet_status",
                state.AccessToken,
                new { vins = vehicles.Select(vehicle => vehicle.Vin).Where(vin => !string.IsNullOrWhiteSpace(vin)).ToArray() },
                checks,
                cancellationToken);
        if (fleetStatus.HasValue)
        {
            vehicles = vehicles
                .Select(vehicle => vehicle with { Values = Merge(vehicle.Values, ReadFleetStatusForVin(fleetStatus.Value, vehicle.Vin)) })
                .ToList();
        }

        if (state.FetchRealtimeVehicleData || state.HomeAssistantMqttEnabled)
        {
            vehicles = await FetchRealtimeVehicleDataAsync(audience, state.AccessToken, vehicles, checks, cancellationToken);
        }
        else
        {
            checks.Add("Realtime vehicle_data calls skipped because the setting is disabled.");
        }

        var energySites = ReadEnergySites(products);
        energySites = await FetchEnergyLiveStatusAsync(audience, state.AccessToken, energySites, checks, cancellationToken);

        return new TeslaFleetSnapshot(
            DateTimeOffset.UtcNow,
            me,
            region,
            products,
            vehicles,
            energySites,
            checks);
    }

    private async Task<List<TeslaVehicleSnapshot>> FetchRealtimeVehicleDataAsync(
        string audience,
        string accessToken,
        IReadOnlyList<TeslaVehicleSnapshot> vehicles,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        var updated = new List<TeslaVehicleSnapshot>();
        foreach (var vehicle in vehicles)
        {
            if (!vehicle.State.Equals("online", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add($"Skipped vehicle_data for {vehicle.DisplayName}: vehicle state is {FirstNonEmpty(vehicle.State, "unknown")}.");
                updated.Add(vehicle);
                continue;
            }

            var realtime = await TryGetResponseAsync(
                audience,
                $"/api/1/vehicles/{Uri.EscapeDataString(FirstNonEmpty(vehicle.Vin, vehicle.Id))}/vehicle_data",
                accessToken,
                checks,
                cancellationToken);
            if (realtime.HasValue)
            {
                var values = Merge(vehicle.Values, FlattenObject(ReadResponse(realtime.Value), null));
                values["lms_helper.vehicle_data_refreshed_utc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                updated.Add(vehicle with { Values = values });
            }
            else
            {
                updated.Add(vehicle);
            }
        }

        return updated;
    }

    private async Task<List<TeslaEnergySiteSnapshot>> FetchEnergyLiveStatusAsync(
        string audience,
        string accessToken,
        IReadOnlyList<TeslaEnergySiteSnapshot> sites,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        var updated = new List<TeslaEnergySiteSnapshot>();
        foreach (var site in sites)
        {
            var values = new Dictionary<string, object?>(site.Values, StringComparer.OrdinalIgnoreCase);
            var siteInfo = await TryGetResponseAsync(
                audience,
                $"/api/1/energy_sites/{Uri.EscapeDataString(site.SiteId)}/site_info",
                accessToken,
                checks,
                cancellationToken);
            if (siteInfo.HasValue)
            {
                values = Merge(values, FlattenObject(ReadResponse(siteInfo.Value), "site_info"));
            }

            var liveStatus = await TryGetResponseAsync(
                audience,
                $"/api/1/energy_sites/{Uri.EscapeDataString(site.SiteId)}/live_status",
                accessToken,
                checks,
                cancellationToken);
            if (liveStatus.HasValue)
            {
                values = Merge(values, FlattenObject(ReadResponse(liveStatus.Value), null));
            }

            updated.Add(site with { Values = values });
        }

        return updated;
    }

    private async Task<List<TeslaVehicleSnapshot>> ReadVehiclesAsync(
        string audience,
        string accessToken,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        var response = await TryGetResponseAsync(audience, "/api/1/vehicles", accessToken, checks, cancellationToken);
        return response.HasValue ? ReadVehicles(response.Value) : [];
    }

    private async Task<JsonElement?> TryGetResponseAsync(
        string audience,
        string path,
        string accessToken,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{audience.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        checks.Add($"GET {path} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        if (!response.IsSuccessStatusCode)
        {
            checks.Add($"GET {path} response: {TruncateForDiagnostics(body)}");
            if (path.StartsWith("/api/1/energy_sites/", StringComparison.OrdinalIgnoreCase) &&
                body.Contains("missing scopes", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add("Tesla energy endpoints require the energy_device_data OAuth scope. Start Tesla OAuth again from the Helper setup page to grant Energy Product Information access.");
            }

            return null;
        }

        return ParseRoot(body);
    }

    private async Task<JsonElement?> TryPostResponseAsync(
        string audience,
        string path,
        string accessToken,
        object payload,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{audience.TrimEnd('/')}{path}")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        checks.Add($"POST {path} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        if (!response.IsSuccessStatusCode)
        {
            checks.Add($"POST {path} response: {TruncateForDiagnostics(body)}");
            return null;
        }

        return ParseRoot(body);
    }

    private static JsonElement ParseRoot(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static JsonElement ReadResponse(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var response)
            ? response
            : root;

    private static List<TeslaVehicleSnapshot> ReadVehicles(JsonElement? root)
    {
        if (!root.HasValue)
        {
            return [];
        }

        var response = ReadResponse(root.Value);
        if (response.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var vehicles = new List<TeslaVehicleSnapshot>();
        foreach (var item in response.EnumerateArray())
        {
            var vin = ReadString(item, "vin") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(vin))
            {
                continue;
            }

            var values = FlattenObject(item, null);
            vehicles.Add(new TeslaVehicleSnapshot(
                vin,
                ReadString(item, "id_s", "id") ?? vin,
                ReadString(item, "display_name", "name") ?? vin,
                ReadString(item, "state") ?? "unknown",
                values));
        }

        return vehicles;
    }

    private static List<TeslaEnergySiteSnapshot> ReadEnergySites(JsonElement? root)
    {
        if (!root.HasValue)
        {
            return [];
        }

        var response = ReadResponse(root.Value);
        if (response.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var sites = new List<TeslaEnergySiteSnapshot>();
        foreach (var item in response.EnumerateArray())
        {
            var siteId = ReadString(item, "energy_site_id", "site_id", "id") ?? string.Empty;
            var resourceType = ReadString(item, "resource_type") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(siteId) ||
                !resourceType.Contains("battery", StringComparison.OrdinalIgnoreCase) &&
                !resourceType.Contains("solar", StringComparison.OrdinalIgnoreCase) &&
                !item.TryGetProperty("energy_site_id", out _))
            {
                continue;
            }

            var values = FlattenObject(item, null);
            sites.Add(new TeslaEnergySiteSnapshot(
                siteId,
                ReadString(item, "site_name", "name") ?? $"Energy Site {siteId}",
                resourceType,
                values));
        }

        return sites;
    }

    private static TeslaVehicleSnapshot MergeVehicle(IReadOnlyList<TeslaVehicleSnapshot> vehicles)
    {
        var first = vehicles[0];
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in vehicles)
        {
            foreach (var item in vehicle.Values)
            {
                if (item.Value is not null)
                {
                    values[item.Key] = item.Value;
                }
            }
        }

        return first with
        {
            Id = FirstNonEmpty(vehicles.Select(vehicle => vehicle.Id).ToArray()),
            DisplayName = FirstNonEmpty(vehicles.Select(vehicle => vehicle.DisplayName).ToArray()),
            State = FirstNonEmpty(vehicles.Select(vehicle => vehicle.State).ToArray()),
            Values = values
        };
    }

    private static Dictionary<string, object?> ReadFleetStatusForVin(JsonElement root, string vin)
    {
        var response = ReadResponse(root);
        if (response.ValueKind == JsonValueKind.Object)
        {
            if (response.TryGetProperty(vin, out var byVin))
            {
                return FlattenObject(byVin, "fleet_status");
            }

            if (response.TryGetProperty("vehicles", out var vehicles) &&
                vehicles.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in vehicles.EnumerateArray())
                {
                    if ((ReadString(item, "vin") ?? string.Empty).Equals(vin, StringComparison.OrdinalIgnoreCase))
                    {
                        return FlattenObject(item, "fleet_status");
                    }
                }
            }
        }
        else if (response.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in response.EnumerateArray())
            {
                if ((ReadString(item, "vin") ?? string.Empty).Equals(vin, StringComparison.OrdinalIgnoreCase))
                {
                    return FlattenObject(item, "fleet_status");
                }
            }
        }

        return [];
    }

    private static Dictionary<string, object?> FlattenObject(JsonElement element, string? prefix)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        FlattenInto(element, prefix, values);
        return values;
    }

    private static void FlattenInto(JsonElement element, string? prefix, Dictionary<string, object?> values)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var key = string.IsNullOrWhiteSpace(prefix) ? property.Name : $"{prefix}.{property.Name}";
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    FlattenInto(property.Value, key, values);
                    break;
                case JsonValueKind.Array:
                    values[key] = property.Value.GetRawText();
                    FlattenArrayInto(property.Value, key, values);
                    break;
                case JsonValueKind.String:
                    values[key] = property.Value.GetString();
                    break;
                case JsonValueKind.Number:
                    values[key] = property.Value.TryGetInt64(out var longValue)
                        ? longValue
                        : property.Value.TryGetDouble(out var doubleValue) ? doubleValue : property.Value.GetRawText();
                    break;
                case JsonValueKind.True:
                    values[key] = true;
                    break;
                case JsonValueKind.False:
                    values[key] = false;
                    break;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    values[key] = null;
                    break;
            }
        }
    }

    private static void FlattenArrayInto(JsonElement element, string prefix, Dictionary<string, object?> values)
    {
        var index = 1;
        foreach (var item in element.EnumerateArray())
        {
            var indexedPrefix = $"{prefix}.{index}";
            switch (item.ValueKind)
            {
                case JsonValueKind.Object:
                    FlattenInto(item, indexedPrefix, values);
                    break;
                case JsonValueKind.Array:
                    values[indexedPrefix] = item.GetRawText();
                    FlattenArrayInto(item, indexedPrefix, values);
                    break;
                default:
                    values[indexedPrefix] = ReadScalar(item);
                    break;
            }

            index++;
        }
    }

    private static object? ReadScalar(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.TryGetDouble(out var doubleValue) ? doubleValue : element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };

    private static string? ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            return property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        return null;
    }

    private static Dictionary<string, object?> Merge(
        IReadOnlyDictionary<string, object?> current,
        IReadOnlyDictionary<string, object?> next)
    {
        var merged = new Dictionary<string, object?>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var item in next)
        {
            merged[item.Key] = item.Value;
        }

        return merged;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string TruncateForDiagnostics(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= 600
            ? value
            : $"{value[..600]}...";
}

sealed class TeslaFleetMqttPublisher(
    TeslaFleetStateMapper stateMapper,
    HomeAssistantMqttProjectionMapper projectionMapper)
{
    private static readonly string[] KnownDiscoveryComponents =
    [
        "sensor",
        "binary_sensor",
        "number",
        "select",
        "switch",
        "button",
        "lock",
        "device_tracker"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<TeslaHomeAssistantPublishResult> PublishAsync(
        TeslaFleetState state,
        TeslaFleetSnapshot snapshot,
        CancellationToken cancellationToken,
        bool resetDiscovery = false)
    {
        if (!state.HomeAssistantMqttEnabled)
        {
            return new TeslaHomeAssistantPublishResult(false, "Home Assistant MQTT publishing is disabled.", [], [], []);
        }

        var settings = TeslaMqttSettings.FromState(state);
        var checks = new List<string>();
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var clientId = $"lms-tesla-fleet-helper-{Environment.MachineName}-{Guid.NewGuid():N}";
        if (clientId.Length > 54)
        {
            clientId = clientId[..54];
        }

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(settings.Host, settings.Port)
            .WithCleanSession();
        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            optionsBuilder = optionsBuilder.WithCredentials(settings.Username, settings.Password);
        }

        await client.ConnectAsync(optionsBuilder.Build(), cancellationToken);
        checks.Add($"Connected to MQTT broker {settings.Host}:{settings.Port}.");

        var normalized = stateMapper.Map(snapshot, state.FleetApiAudience);
        var projection = projectionMapper.Map(normalized, settings.BaseTopic);
        if (resetDiscovery)
        {
            var resetResult = await ResetHomeAssistantDiscoveryAsync(client, settings, projection, state.LastHomeAssistantDiscoveryTopics ?? [], cancellationToken);
            checks.Add(
                $"Reset Home Assistant MQTT discovery: cleared {resetResult.DiscoveryTopicCount} retained discovery config topic(s); retained state topics were left intact.");
        }

        await PublishStringAsync(client, $"{settings.BaseTopic}/availability", "online", retain: true, cancellationToken);
        var devices = projection.Devices.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
        var discoveryTopics = new List<string>();
        var publishedByDevice = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in projection.Entities)
        {
            if (!devices.TryGetValue(entity.DeviceId, out var device))
            {
                continue;
            }

            await PublishEntityDiscoveryAsync(client, settings, entity, device, cancellationToken);
            publishedByDevice[device.Name] = publishedByDevice.GetValueOrDefault(device.Name) + 1;
            discoveryTopics.Add(BuildDiscoveryTopic(settings, entity));
        }
        var retiredEnergyDiscoveryCount = await PublishRetiredDiscoveryAsync(client, BuildRetiredEnergyDiscoveryTopics(settings, projection), cancellationToken);
        var retiredVehicleDiscoveryCount = await PublishRetiredDiscoveryAsync(client, BuildRetiredVehicleDiscoveryTopics(settings, projection), cancellationToken);

        var publishedStates = BuildPublishedStatePayloads(projection, state.LastHomeAssistantStatePayloads ?? []);
        foreach (var stateProjection in publishedStates)
        {
            await PublishJsonAsync(client, stateProjection.Topic, stateProjection.Payload, retain: true, cancellationToken);
        }

        await client.DisconnectAsync(cancellationToken: cancellationToken);
        checks.Add($"Discovery prefix: {settings.DiscoveryPrefix}; base topic: {settings.BaseTopic}.");
        checks.Add($"Published {projection.Entities.Count} MQTT discovery config(s) for {projection.Devices.Count} device(s).");
        if (publishedByDevice.Count > 0)
        {
            checks.Add($"Discovery configs by device: {string.Join(", ", publishedByDevice.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item => $"{item.Key}={item.Value}"))}.");
        }
        if (retiredEnergyDiscoveryCount > 0)
        {
            checks.Add($"Cleared {retiredEnergyDiscoveryCount} retired Energy MQTT discovery config(s).");
        }
        if (retiredVehicleDiscoveryCount > 0)
        {
            checks.Add($"Cleared {retiredVehicleDiscoveryCount} retired vehicle MQTT discovery config(s).");
        }
        checks.Add($"Published {publishedStates.Count} retained state topic(s).");
        if (discoveryTopics.Count > 0)
        {
            checks.Add($"Sample discovery topics: {string.Join(", ", discoveryTopics.Take(6))}.");
        }
        if (publishedStates.Count > 0)
        {
            checks.Add($"State topics: {string.Join(", ", publishedStates.Select(topic => topic.Topic).Take(6))}.");
        }
        checks.AddRange(BuildVehicleStatePayloadChecks(publishedStates));
        checks.Add($"Snapshot source contained {snapshot.Vehicles.Count} vehicle(s) and {snapshot.EnergySites.Count} energy site(s).");
        checks.AddRange(snapshot.Checks);
        return new TeslaHomeAssistantPublishResult(
            true,
            $"Published {projection.Entities.Count} Home Assistant MQTT Discovery config(s) from typed LMS Tesla projection.",
            checks,
            discoveryTopics,
            BuildStatePayloadCache(publishedStates));
    }

    private static async Task PublishEntityDiscoveryAsync(
        IMqttClient client,
        TeslaMqttSettings settings,
        HomeAssistantMqttEntityProjection entity,
        HomeAssistantMqttDeviceProjection device,
        CancellationToken cancellationToken)
    {
        var devicePayload = new Dictionary<string, object?>
        {
            ["identifiers"] = new[] { device.Id },
            ["name"] = device.Name,
            ["manufacturer"] = device.Manufacturer,
            ["model"] = device.Model
        };
        if (!string.IsNullOrWhiteSpace(device.SoftwareVersion))
        {
            devicePayload["sw_version"] = device.SoftwareVersion;
        }
        if (!string.IsNullOrWhiteSpace(device.ViaDeviceId))
        {
            devicePayload["via_device"] = device.ViaDeviceId;
        }
        var payload = new Dictionary<string, object?>
        {
            ["name"] = entity.Name,
            ["unique_id"] = entity.Id,
            ["availability_topic"] = $"{settings.BaseTopic}/availability",
            ["device"] = devicePayload
        };
        if (!string.IsNullOrWhiteSpace(entity.StateTopic))
        {
            payload["state_topic"] = entity.StateTopic;
        }
        if (!string.IsNullOrWhiteSpace(entity.ValueTemplate))
        {
            payload["value_template"] = entity.ValueTemplate;
        }
        if (!string.IsNullOrWhiteSpace(entity.DeviceClass))
        {
            payload["device_class"] = entity.DeviceClass;
        }
        if (!string.IsNullOrWhiteSpace(entity.UnitOfMeasurement))
        {
            payload["unit_of_measurement"] = entity.UnitOfMeasurement;
        }
        if (!string.IsNullOrWhiteSpace(entity.StateClass))
        {
            payload["state_class"] = entity.StateClass;
        }
        if (!string.IsNullOrWhiteSpace(entity.EntityCategory))
        {
            payload["entity_category"] = entity.EntityCategory;
        }
        if (!string.IsNullOrWhiteSpace(entity.Icon))
        {
            payload["icon"] = entity.Icon;
        }
        if (!string.IsNullOrWhiteSpace(entity.CommandTopic))
        {
            payload["command_topic"] = entity.CommandTopic;
        }
        if (!string.IsNullOrWhiteSpace(entity.CommandTemplate))
        {
            payload["command_template"] = entity.CommandTemplate;
        }
        if (!entity.EnabledByDefault)
        {
            payload["enabled_by_default"] = false;
        }
        if (entity.ExtraConfig is not null)
        {
            foreach (var item in entity.ExtraConfig)
            {
                payload[item.Key] = item.Value;
            }
        }

        await PublishJsonAsync(client, BuildDiscoveryTopic(settings, entity), payload, retain: true, cancellationToken);
    }

    private static List<HomeAssistantMqttStateProjection> BuildPublishedStatePayloads(
        HomeAssistantMqttProjection projection,
        IReadOnlyCollection<HomeAssistantStatePayloadCacheEntry> cachedStatePayloads)
    {
        var cachedByTopic = cachedStatePayloads
            .Where(item => !string.IsNullOrWhiteSpace(item.Topic) && !string.IsNullOrWhiteSpace(item.PayloadJson))
            .GroupBy(item => item.Topic, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedUtc).First(), StringComparer.OrdinalIgnoreCase);

        return projection.States
            .Select(state =>
            {
                if (!state.Topic.Contains("/vehicles/", StringComparison.OrdinalIgnoreCase) ||
                    !cachedByTopic.TryGetValue(state.Topic, out var cached) ||
                    !TryReadCachedPayload(cached.PayloadJson, out var cachedPayload))
                {
                    return state;
                }

                return state with { Payload = MergeCachedPayload(cachedPayload, state.Payload) };
            })
            .ToList();
    }

    private static IReadOnlyDictionary<string, object?> MergeCachedPayload(
        IReadOnlyDictionary<string, object?> cachedPayload,
        IReadOnlyDictionary<string, object?> currentPayload)
    {
        var merged = new Dictionary<string, object?>(cachedPayload, StringComparer.OrdinalIgnoreCase);
        foreach (var item in currentPayload)
        {
            if (IsUsefulStateValue(item.Value))
            {
                merged[item.Key] = item.Value;
            }
        }

        return merged;
    }

    private static bool IsUsefulStateValue(object? value) =>
        value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            _ => true
        };

    private static List<HomeAssistantStatePayloadCacheEntry> BuildStatePayloadCache(
        IReadOnlyCollection<HomeAssistantMqttStateProjection> states)
    {
        var now = DateTimeOffset.UtcNow;
        return states
            .Where(state => state.Topic.Contains("/vehicles/", StringComparison.OrdinalIgnoreCase))
            .Select(state => new HomeAssistantStatePayloadCacheEntry(
                state.Topic,
                JsonSerializer.Serialize(state.Payload, JsonOptions),
                now))
            .ToList();
    }

    private static bool TryReadCachedPayload(string payloadJson, out IReadOnlyDictionary<string, object?> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                payload = new Dictionary<string, object?>();
                return false;
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = ReadCachedJsonValue(property.Value);
            }

            payload = values;
            return true;
        }
        catch (JsonException)
        {
            payload = new Dictionary<string, object?>();
            return false;
        }
    }

    private static object? ReadCachedJsonValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var longValue)
                ? longValue
                : value.TryGetDouble(out var doubleValue) ? doubleValue : value.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };

    private static IEnumerable<string> BuildVehicleStatePayloadChecks(IEnumerable<HomeAssistantMqttStateProjection> states)
    {
        foreach (var state in states.Where(state => state.Topic.Contains("/vehicles/", StringComparison.OrdinalIgnoreCase)).Take(6))
        {
            state.Payload.TryGetValue("display_name", out var displayName);
            state.Payload.TryGetValue("charge_limit", out var chargeLimit);
            state.Payload.TryGetValue("charging_amps", out var chargingAmps);
            state.Payload.TryGetValue("battery_level", out var batteryLevel);
            state.Payload.TryGetValue("charging_state", out var chargingState);
            state.Payload.TryGetValue("vehicle_data_refreshed", out var vehicleDataRefreshed);
            yield return
                $"Vehicle HA state payload {displayName ?? state.Topic}: battery_level={FormatCheckValue(batteryLevel)}, charging_state={FormatCheckValue(chargingState)}, charge_limit={FormatCheckValue(chargeLimit)}, charging_amps={FormatCheckValue(chargingAmps)}, vehicle_data_refreshed={FormatCheckValue(vehicleDataRefreshed)}.";
        }
    }

    private static string FormatCheckValue(object? value) =>
        value switch
        {
            null => "null",
            string text when string.IsNullOrWhiteSpace(text) => "blank",
            _ => value.ToString() ?? "null"
        };

    private static async Task<int> PublishRetiredDiscoveryAsync(
        IMqttClient client,
        IEnumerable<string> topics,
        CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var topic in topics.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await PublishStringAsync(client, topic, string.Empty, retain: true, cancellationToken);
            count++;
        }

        return count;
    }

    private static string BuildDiscoveryTopic(TeslaMqttSettings settings, HomeAssistantMqttEntityProjection entity) =>
        BuildDiscoveryTopic(settings, entity.Component, entity.Id);

    private static string BuildDiscoveryTopic(TeslaMqttSettings settings, string component, string entityId) =>
        $"{settings.DiscoveryPrefix}/{component}/lms_tesla_fleet/{entityId}/config";

    private static async Task<(int DiscoveryTopicCount, int StateTopicCount)> ResetHomeAssistantDiscoveryAsync(
        IMqttClient client,
        TeslaMqttSettings settings,
        HomeAssistantMqttProjection projection,
        IReadOnlyCollection<string> previousDiscoveryTopics,
        CancellationToken cancellationToken)
    {
        var discoveryTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var topic in previousDiscoveryTopics)
        {
            if (!string.IsNullOrWhiteSpace(topic))
            {
                discoveryTopics.Add(topic.Trim());
            }
        }

        foreach (var entity in projection.Entities)
        {
            discoveryTopics.Add(BuildDiscoveryTopic(settings, entity));
            foreach (var component in KnownDiscoveryComponents)
            {
                discoveryTopics.Add(BuildDiscoveryTopic(settings, component, entity.Id));
            }
        }

        foreach (var topic in BuildRetiredEnergyDiscoveryTopics(settings, projection))
        {
            discoveryTopics.Add(topic);
        }
        foreach (var topic in BuildRetiredVehicleDiscoveryTopics(settings, projection))
        {
            discoveryTopics.Add(topic);
        }

        foreach (var topic in discoveryTopics.Order(StringComparer.OrdinalIgnoreCase))
        {
            await PublishStringAsync(client, topic, string.Empty, retain: true, cancellationToken);
        }

        return (discoveryTopics.Count, 0);
    }

    private static List<string> BuildRetiredEnergyDiscoveryTopics(
        TeslaMqttSettings settings,
        HomeAssistantMqttProjection projection)
    {
        var energyDeviceIds = projection.Devices
            .Select(device => device.Id)
            .Where(id => id.StartsWith("lms_tesla_energy_", StringComparison.OrdinalIgnoreCase) &&
                         !id.Contains("_powerwall_", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var retired = new (string Component, string IdSuffix)[]
        {
            ("number", "backup_reserve_target"),
            ("switch", "grid_charging"),
            ("select", "energy_export_rule")
        };
        return energyDeviceIds
            .SelectMany(deviceId => retired.Select(item => BuildDiscoveryTopic(settings, item.Component, $"{deviceId}_{item.IdSuffix}")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildRetiredVehicleDiscoveryTopics(
        TeslaMqttSettings settings,
        HomeAssistantMqttProjection projection)
    {
        var vehicleDeviceIds = projection.Devices
            .Select(device => device.Id)
            .Where(id => id.StartsWith("lms_tesla_", StringComparison.OrdinalIgnoreCase) &&
                         !id.StartsWith("lms_tesla_energy_", StringComparison.OrdinalIgnoreCase) &&
                         !id.Equals("lms_tesla_fleet_helper", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var retired = new (string Component, string IdSuffix)[]
        {
            ("sensor", "charge_limit"),
            ("sensor", "charging_amps"),
            ("number", "charge_limit_number"),
            ("number", "charging_amps_number")
        };
        return vehicleDeviceIds
            .SelectMany(deviceId => retired.Select(item => BuildDiscoveryTopic(settings, item.Component, $"{deviceId}_{item.IdSuffix}")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task PublishJsonAsync(
        IMqttClient client,
        string topic,
        object payload,
        bool retain,
        CancellationToken cancellationToken) =>
        await PublishStringAsync(client, topic, JsonSerializer.Serialize(payload, JsonOptions), retain, cancellationToken);

    private static async Task PublishStringAsync(
        IMqttClient client,
        string topic,
        string payload,
        bool retain,
        CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();
        await client.PublishAsync(message, cancellationToken);
    }

    private static string SafeTopic(string value) =>
        string.Join('_', (value ?? string.Empty).Trim().Split([' ', '/', '\\', '#', '+', ':', ';'], StringSplitOptions.RemoveEmptyEntries));

    private static string SafeId(string value)
    {
        var chars = (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }
}

sealed class TeslaFleetVehicleCommandProxyService(
    TeslaFleetStore store,
    ILogger<TeslaFleetVehicleCommandProxyService> logger) : BackgroundService
{
    private const string ProxyHost = "127.0.0.1";
    private const int ProxyPort = 4443;
    private Process? proxyProcess;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var state = await store.LoadAsync(stoppingToken);
                if (string.IsNullOrWhiteSpace(state.PrivateKeyPath) || !File.Exists(state.PrivateKeyPath))
                {
                    await StopProxyAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                if (proxyProcess is null || proxyProcess.HasExited)
                {
                    EnsureProxyTlsFiles();
                    StartProxy(state.PrivateKeyPath);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Tesla vehicle command proxy supervision failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await StopProxyAsync(CancellationToken.None);
    }

    private void StartProxy(string privateKeyPath)
    {
        var executable = Environment.GetEnvironmentVariable("TeslaFleetHelper__VehicleCommandProxyExecutable") ??
                         "tesla-http-proxy";
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-tls-key");
        startInfo.ArgumentList.Add(store.VehicleCommandProxyTlsKeyPath);
        startInfo.ArgumentList.Add("-cert");
        startInfo.ArgumentList.Add(store.VehicleCommandProxyTlsCertPath);
        startInfo.ArgumentList.Add("-key-file");
        startInfo.ArgumentList.Add(privateKeyPath);
        startInfo.ArgumentList.Add("-host");
        startInfo.ArgumentList.Add(ProxyHost);
        startInfo.ArgumentList.Add("-port");
        startInfo.ArgumentList.Add(ProxyPort.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-session-cache");
        startInfo.ArgumentList.Add(store.VehicleCommandProxyCachePath);
        startInfo.Environment["TESLA_CACHE_FILE"] = store.VehicleCommandProxyCachePath;

        proxyProcess = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        proxyProcess.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                logger.LogInformation("tesla-http-proxy: {Message}", args.Data);
            }
        };
        proxyProcess.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                logger.LogInformation("tesla-http-proxy: {Message}", args.Data);
            }
        };
        proxyProcess.Exited += (_, _) =>
            logger.LogWarning("Tesla vehicle command proxy exited with code {ExitCode}.", proxyProcess?.ExitCode);

        if (!proxyProcess.Start())
        {
            throw new InvalidOperationException("Tesla vehicle command proxy did not start.");
        }

        proxyProcess.BeginOutputReadLine();
        proxyProcess.BeginErrorReadLine();
        logger.LogInformation("Started Tesla vehicle command proxy on https://{Host}:{Port}.", ProxyHost, ProxyPort);
    }

    private void EnsureProxyTlsFiles()
    {
        if (File.Exists(store.VehicleCommandProxyTlsCertPath) &&
            File.Exists(store.VehicleCommandProxyTlsKeyPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(store.VehicleCommandProxyTlsCertPath)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1")
            },
            critical: false));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        File.WriteAllText(store.VehicleCommandProxyTlsCertPath, certificate.ExportCertificatePem());
        File.WriteAllText(store.VehicleCommandProxyTlsKeyPath, key.ExportPkcs8PrivateKeyPem());
        TrySetOwnerOnly(store.VehicleCommandProxyTlsKeyPath);
    }

    private async Task StopProxyAsync(CancellationToken cancellationToken)
    {
        if (proxyProcess is null)
        {
            return;
        }

        try
        {
            if (!proxyProcess.HasExited)
            {
                proxyProcess.Kill(entireProcessTree: true);
                await proxyProcess.WaitForExitAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Failed to stop Tesla vehicle command proxy cleanly.");
        }
        finally
        {
            proxyProcess.Dispose();
            proxyProcess = null;
        }
    }

    private static void TrySetOwnerOnly(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Non-Unix/local dev filesystems can ignore this; Home Assistant runs on Linux.
        }
    }
}

sealed class TeslaFleetEnergyCommandClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] OperationModes =
    [
        "self_consumption",
        "autonomous",
        "backup",
        "Self-Powered",
        "Time-Based Control",
        "Backup"
    ];

    private static readonly string[] ExportRules =
    [
        "never",
        "pv_only",
        "battery_ok",
        "Nothing",
        "Solar",
        "Everything"
    ];

    public async Task<TeslaEnergyCommandResult> ExecuteAsync(
        TeslaFleetState state,
        string siteId,
        string action,
        string payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            throw new InvalidOperationException("Complete Tesla OAuth before sending Energy commands.");
        }

        if (string.IsNullOrWhiteSpace(siteId))
        {
            throw new InvalidOperationException("Energy command topic did not contain a site ID.");
        }

        var normalizedAction = action.Trim().ToLowerInvariant();
        var value = NormalizePayload(payload);
        var checks = new List<string>
        {
            $"Received Energy command '{normalizedAction}' for site {siteId} with payload '{value}'."
        };

        var command = BuildCommand(siteId, normalizedAction, value);
        var result = await PostCommandAsync(
            TeslaFleetDefaults.NormalizeHttpUrl(state.FleetApiAudience, TeslaFleetDefaults.DefaultFleetApiAudience),
            command.Path,
            state.AccessToken,
            command.Payload,
            siteId,
            command.StatePatch,
            checks,
            cancellationToken);

        return result with
        {
            Summary = result.Succeeded
                ? $"{command.DisplayName} command accepted for Energy site {siteId}."
                : $"{command.DisplayName} command failed for Energy site {siteId}."
        };
    }

    private static TeslaEnergyCommand BuildCommand(string siteId, string action, string value)
    {
        var escapedSiteId = Uri.EscapeDataString(siteId);
        return action switch
        {
            "backup_reserve" => BuildBackupReserveCommand(escapedSiteId, value),
            "off_grid_vehicle_charging_reserve" => BuildOffGridVehicleChargingReserveCommand(escapedSiteId, value),
            "operation_mode" => BuildOperationModeCommand(escapedSiteId, value),
            "grid_charging" => BuildGridChargingCommand(escapedSiteId, value),
            "energy_exports" or "energy_export_rule" => BuildEnergyExportsCommand(escapedSiteId, value),
            "storm_mode" => BuildStormModeCommand(escapedSiteId, value),
            _ => throw new InvalidOperationException($"Unsupported Energy command action '{action}'.")
        };
    }

    private static TeslaEnergyCommand BuildBackupReserveCommand(string escapedSiteId, string value)
    {
        var percent = ParsePercent(value);
        return new TeslaEnergyCommand(
            "Backup reserve",
            $"/api/1/energy_sites/{escapedSiteId}/backup",
            new { backup_reserve_percent = percent },
            new Dictionary<string, object?>
            {
                ["backup_reserve_percent"] = percent,
                ["site_info.backup_reserve_percent"] = percent,
                ["site_info.backup.backup_reserve_percent"] = percent
            });
    }

    private static TeslaEnergyCommand BuildOperationModeCommand(string escapedSiteId, string value)
    {
        var mode = MapOperationMode(value);
        return new TeslaEnergyCommand(
            "Operation mode",
            $"/api/1/energy_sites/{escapedSiteId}/operation",
            new { default_real_mode = mode },
            new Dictionary<string, object?>
            {
                ["default_real_mode"] = mode,
                ["site_info.default_real_mode"] = mode,
                ["site_info.operation"] = mode
            });
    }

    private static TeslaEnergyCommand BuildOffGridVehicleChargingReserveCommand(string escapedSiteId, string value)
    {
        var percent = ParsePercent(value);
        return new TeslaEnergyCommand(
            "Off-grid vehicle charging reserve",
            $"/api/1/energy_sites/{escapedSiteId}/off_grid_vehicle_charging_reserve",
            new { off_grid_vehicle_charging_reserve_percent = percent },
            new Dictionary<string, object?>
            {
                ["off_grid_vehicle_charging_reserve"] = percent,
                ["off_grid_vehicle_charging_reserve_percent"] = percent,
                ["site_info.off_grid_vehicle_charging_reserve"] = percent,
                ["site_info.off_grid_vehicle_charging_reserve_percent"] = percent
            });
    }

    private static TeslaEnergyCommand BuildStormModeCommand(string escapedSiteId, string value)
    {
        var enabled = ParseBoolean(value);
        return new TeslaEnergyCommand(
            "Storm watch",
            $"/api/1/energy_sites/{escapedSiteId}/storm_mode",
            new { enabled },
            new Dictionary<string, object?>
            {
                ["storm_mode_active"] = enabled,
                ["site_info.storm_mode_active"] = enabled
            });
    }

    private static TeslaEnergyCommand BuildGridChargingCommand(string escapedSiteId, string value)
    {
        var enabled = ParseBoolean(value);
        var disallowChargeFromGrid = !enabled;
        return new TeslaEnergyCommand(
            "Grid charging",
            $"/api/1/energy_sites/{escapedSiteId}/grid_import_export",
            new { disallow_charge_from_grid_with_solar_installed = disallowChargeFromGrid },
            new Dictionary<string, object?>
            {
                ["disallow_charge_from_grid_with_solar_installed"] = disallowChargeFromGrid,
                ["site_info.components.disallow_charge_from_grid_with_solar_installed"] = disallowChargeFromGrid
            });
    }

    private static TeslaEnergyCommand BuildEnergyExportsCommand(string escapedSiteId, string value)
    {
        var exportRule = MapEnergyExports(value);
        return new TeslaEnergyCommand(
            "Energy exports",
            $"/api/1/energy_sites/{escapedSiteId}/grid_import_export",
            new { customer_preferred_export_rule = exportRule },
            new Dictionary<string, object?>
            {
                ["customer_preferred_export_rule"] = exportRule,
                ["site_info.components.customer_preferred_export_rule"] = exportRule
            });
    }

    private async Task<TeslaEnergyCommandResult> PostCommandAsync(
        string audience,
        string path,
        string accessToken,
        object payload,
        string siteId,
        IReadOnlyDictionary<string, object?> statePatch,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{audience.TrimEnd('/')}{path}")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        checks.Add($"POST {path} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        if (!response.IsSuccessStatusCode)
        {
            checks.Add($"POST {path} response: {TruncateForDiagnostics(body)}");
            if (body.Contains("missing scopes", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add("Tesla Energy writes require the energy_cmds OAuth scope. Start Tesla OAuth again from the Helper setup page so writable Energy controls can be authorized.");
            }

            return new TeslaEnergyCommandResult(false, "Tesla Energy command failed.", checks, siteId, new Dictionary<string, object?>());
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            checks.Add($"POST {path} response: {TruncateForDiagnostics(body)}");
            if (!IsAcceptedCommandResponse(body, checks))
            {
                return new TeslaEnergyCommandResult(false, "Tesla Energy command was not accepted by the Fleet API response body.", checks, siteId, new Dictionary<string, object?>());
            }
        }

        return new TeslaEnergyCommandResult(true, "Tesla Energy command accepted.", checks, siteId, statePatch);
    }

    private static string NormalizePayload(string payload)
    {
        var value = (payload ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.String => document.RootElement.GetString()?.Trim() ?? string.Empty,
                JsonValueKind.Number => document.RootElement.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.Trim('"')
            };
        }
        catch (JsonException)
        {
            return value.Trim('"');
        }
    }

    private static int ParsePercent(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            !double.TryParse(value, out parsed))
        {
            throw new InvalidOperationException($"Backup reserve must be a whole percentage from 0 to 100. Received '{value}'.");
        }

        var percent = (int)Math.Round(parsed);
        if (percent is < 0 or > 100)
        {
            throw new InvalidOperationException($"Backup reserve must be between 0 and 100. Received '{percent}'.");
        }

        return percent;
    }

    private static bool ParseBoolean(string value)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        if (value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidOperationException($"Expected true/false payload. Received '{value}'.");
    }

    private static string MapOperationMode(string value)
    {
        var normalized = RequireAllowed(value, OperationModes, "operation mode");
        return normalized switch
        {
            "Self-Powered" => "self_consumption",
            "Time-Based Control" => "autonomous",
            "Backup" => "backup",
            _ => normalized
        };
    }

    private static string MapEnergyExports(string value)
    {
        var normalized = RequireAllowed(value, ExportRules, "energy exports");
        return normalized switch
        {
            "Nothing" => "never",
            "Solar" => "pv_only",
            "Everything" => "battery_ok",
            _ => normalized
        };
    }

    private static string RequireAllowed(string value, IReadOnlyList<string> allowed, string label)
    {
        var normalized = value.Trim();
        if (allowed.Any(item => item.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return allowed.First(item => item.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        }

        throw new InvalidOperationException($"Unsupported {label} '{value}'. Allowed values: {string.Join(", ", allowed)}.");
    }

    private static bool IsAcceptedCommandResponse(string body, List<string> checks)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var response = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var responseElement)
                ? responseElement
                : root;
            if (response.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            if (TryReadInt(response, "code", out var code))
            {
                checks.Add($"Tesla command response code: {code}.");
                return code is >= 200 and < 300;
            }

            if (TryReadBool(response, "result", out var result))
            {
                checks.Add($"Tesla command response result: {result}.");
                return result;
            }

            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), out value);
    }

    private static bool TryReadBool(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               bool.TryParse(property.GetString(), out value);
    }

    private static string TruncateForDiagnostics(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= 600
            ? value
            : $"{value[..600]}...";
}

sealed class TeslaFleetVehicleCommandClient(HttpClient httpClient)
{
    private const string ProxyBaseUrl = "https://127.0.0.1:4443";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<TeslaVehicleCommandResult> ExecuteAsync(
        TeslaFleetState state,
        string vin,
        string action,
        string payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            throw new InvalidOperationException("Complete Tesla OAuth before sending vehicle commands.");
        }

        if (string.IsNullOrWhiteSpace(vin))
        {
            throw new InvalidOperationException("Vehicle command topic did not contain a VIN.");
        }

        if (string.IsNullOrWhiteSpace(state.PrivateKeyPath) || !File.Exists(state.PrivateKeyPath))
        {
            throw new InvalidOperationException("Generate and publish the Tesla Fleet virtual key before sending vehicle commands.");
        }

        var normalizedAction = action.Trim().ToLowerInvariant();
        var value = NormalizePayload(payload);
        var command = BuildCommand(vin, normalizedAction, value);
        var checks = new List<string>
        {
            $"Received vehicle command '{normalizedAction}' for VIN {vin} with payload '{value}'.",
            command.UsesProxy
                ? "Sending through Tesla's official vehicle-command HTTP proxy so the request is signed with the installed virtual key."
                : "Sending directly to the Fleet API because this vehicle endpoint is not a signed command endpoint."
        };

        var baseUrl = command.UsesProxy
            ? ProxyBaseUrl
            : TeslaFleetDefaults.NormalizeHttpUrl(state.FleetApiAudience, TeslaFleetDefaults.DefaultFleetApiAudience);
        var result = await PostCommandAsync(
            baseUrl,
            command.Path,
            state.AccessToken,
            command.Payload,
            vin,
            command.StatePatch,
            checks,
            cancellationToken);

        return result with
        {
            Summary = result.Succeeded
                ? $"{command.DisplayName} command accepted for vehicle {vin}."
                : $"{command.DisplayName} command failed for vehicle {vin}."
        };
    }

    private static TeslaVehicleCommand BuildCommand(string vin, string action, string value)
    {
        var escapedVin = Uri.EscapeDataString(vin);
        return action switch
        {
            "charge_limit" => BuildChargeLimitCommand(escapedVin, value),
            "charging_amps" => BuildChargingAmpsCommand(escapedVin, value),
            "charger" => BuildChargerCommand(escapedVin, value),
            "climate" => BuildClimateCommand(escapedVin, value),
            "sentry_mode" => BuildSentryModeCommand(escapedVin, value),
            "door_lock" => BuildDoorLockCommand(escapedVin, value),
            "wake_up" => new TeslaVehicleCommand("Wake up", $"/api/1/vehicles/{escapedVin}/wake_up", new { }, new Dictionary<string, object?>(), UsesProxy: false),
            "flash_lights" => EmptyProxyCommand("Flash lights", escapedVin, "flash_lights"),
            "honk_horn" => EmptyProxyCommand("Horn", escapedVin, "honk_horn"),
            "charge_port_door_open" => BuildChargePortCommand(escapedVin, open: true),
            "charge_port_door_close" => BuildChargePortCommand(escapedVin, open: false),
            "open_frunk" => BuildTrunkCommand(escapedVin, "front", "Open frunk"),
            "open_trunk" => BuildTrunkCommand(escapedVin, "rear", "Open trunk"),
            _ => throw new InvalidOperationException($"Unsupported vehicle command action '{action}'.")
        };
    }

    private static TeslaVehicleCommand BuildChargeLimitCommand(string escapedVin, string value)
    {
        var percent = ParsePercent(value, min: 50, max: 100, label: "charge limit");
        return new TeslaVehicleCommand(
            "Charge limit",
            $"/api/1/vehicles/{escapedVin}/command/set_charge_limit",
            new { percent },
            new Dictionary<string, object?>
            {
                ["charge_state.charge_limit_soc"] = percent,
                ["charge_state.ChargeLimitSoc"] = percent,
                ["charge_limit_soc"] = percent,
                ["ChargeLimitSoc"] = percent
            },
            UsesProxy: true);
    }

    private static TeslaVehicleCommand BuildChargingAmpsCommand(string escapedVin, string value)
    {
        var amps = ParsePercent(value, min: 1, max: 80, label: "charging amps");
        return new TeslaVehicleCommand(
            "Charging amps",
            $"/api/1/vehicles/{escapedVin}/command/set_charging_amps",
            new { charging_amps = amps },
            new Dictionary<string, object?>
            {
                ["charge_state.charge_current_request"] = amps,
                ["charge_state.ChargeCurrentRequest"] = amps,
                ["charge_state.charge_amps"] = amps,
                ["charge_state.ChargeAmps"] = amps,
                ["charge_state.charging_amps"] = amps,
                ["charge_current_request"] = amps,
                ["ChargeCurrentRequest"] = amps,
                ["charge_amps"] = amps,
                ["ChargeAmps"] = amps,
                ["charging_amps"] = amps
            },
            UsesProxy: true);
    }

    private static TeslaVehicleCommand BuildChargerCommand(string escapedVin, string value)
    {
        var enabled = ParseOnOff(value);
        return new TeslaVehicleCommand(
            enabled ? "Start charging" : "Stop charging",
            $"/api/1/vehicles/{escapedVin}/command/{(enabled ? "charge_start" : "charge_stop")}",
            new { },
            new Dictionary<string, object?>
            {
                ["charge_state.charging_state"] = enabled ? "Charging" : "Stopped",
                ["charging_state"] = enabled ? "Charging" : "Stopped"
            },
            UsesProxy: true);
    }

    private static TeslaVehicleCommand BuildClimateCommand(string escapedVin, string value)
    {
        var enabled = ParseOnOff(value);
        return new TeslaVehicleCommand(
            enabled ? "Start climate" : "Stop climate",
            $"/api/1/vehicles/{escapedVin}/command/{(enabled ? "auto_conditioning_start" : "auto_conditioning_stop")}",
            new { },
            new Dictionary<string, object?>
            {
                ["climate_state.is_climate_on"] = enabled,
                ["is_climate_on"] = enabled
            },
            UsesProxy: true);
    }

    private static TeslaVehicleCommand BuildSentryModeCommand(string escapedVin, string value)
    {
        var enabled = ParseOnOff(value);
        return new TeslaVehicleCommand(
            "Sentry mode",
            $"/api/1/vehicles/{escapedVin}/command/set_sentry_mode",
            new { on = enabled },
            new Dictionary<string, object?>
            {
                ["vehicle_state.sentry_mode"] = enabled,
                ["sentry_mode"] = enabled
            },
            UsesProxy: true);
    }

    private static TeslaVehicleCommand BuildDoorLockCommand(string escapedVin, string value)
    {
        var lockDoors = value.Equals("LOCK", StringComparison.OrdinalIgnoreCase) || ParseOnOff(value);
        return new TeslaVehicleCommand(
            lockDoors ? "Lock doors" : "Unlock doors",
            $"/api/1/vehicles/{escapedVin}/command/{(lockDoors ? "door_lock" : "door_unlock")}",
            new { },
            new Dictionary<string, object?>
            {
                ["vehicle_state.locked"] = lockDoors,
                ["locked"] = lockDoors
            },
            UsesProxy: true);
    }

    private static TeslaVehicleCommand BuildChargePortCommand(string escapedVin, bool open) =>
        new(
            open ? "Open charge port" : "Close charge port",
            $"/api/1/vehicles/{escapedVin}/command/{(open ? "charge_port_door_open" : "charge_port_door_close")}",
            new { },
            new Dictionary<string, object?>
            {
                ["charge_state.charge_port_door_open"] = open,
                ["charge_port_door_open"] = open
            },
            UsesProxy: true);

    private static TeslaVehicleCommand BuildTrunkCommand(string escapedVin, string whichTrunk, string displayName) =>
        new(
            displayName,
            $"/api/1/vehicles/{escapedVin}/command/actuate_trunk",
            new { which_trunk = whichTrunk },
            new Dictionary<string, object?>(),
            UsesProxy: true);

    private static TeslaVehicleCommand EmptyProxyCommand(string displayName, string escapedVin, string command) =>
        new(
            displayName,
            $"/api/1/vehicles/{escapedVin}/command/{command}",
            new { },
            new Dictionary<string, object?>(),
            UsesProxy: true);

    private async Task<TeslaVehicleCommandResult> PostCommandAsync(
        string baseUrl,
        string path,
        string accessToken,
        object payload,
        string vin,
        IReadOnlyDictionary<string, object?> statePatch,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}{path}")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        checks.Add($"POST {path} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        if (!response.IsSuccessStatusCode)
        {
            checks.Add($"POST {path} response: {TruncateForDiagnostics(body)}");
            if (body.Contains("Vehicle Command Protocol", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add("Tesla rejected an unsigned command. The local vehicle-command proxy must be running and the virtual key must be installed on this vehicle.");
            }

            return new TeslaVehicleCommandResult(false, "Tesla vehicle command failed.", checks, vin, new Dictionary<string, object?>());
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            checks.Add($"POST {path} response: {TruncateForDiagnostics(body)}");
            if (!IsAcceptedCommandResponse(body, checks))
            {
                return new TeslaVehicleCommandResult(false, "Tesla vehicle command was not accepted by the Fleet API response body.", checks, vin, new Dictionary<string, object?>());
            }
        }

        return new TeslaVehicleCommandResult(true, "Tesla vehicle command accepted.", checks, vin, statePatch);
    }

    private static string NormalizePayload(string payload)
    {
        var value = (payload ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.String => document.RootElement.GetString()?.Trim() ?? string.Empty,
                JsonValueKind.Number => document.RootElement.GetRawText(),
                JsonValueKind.True => "ON",
                JsonValueKind.False => "OFF",
                _ => value.Trim('"')
            };
        }
        catch (JsonException)
        {
            return value.Trim('"');
        }
    }

    private static int ParsePercent(string value, int min, int max, string label)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            !double.TryParse(value, out parsed))
        {
            throw new InvalidOperationException($"{label} must be a whole number from {min} to {max}. Received '{value}'.");
        }

        var number = (int)Math.Round(parsed);
        if (number < min || number > max)
        {
            throw new InvalidOperationException($"{label} must be between {min} and {max}. Received '{number}'.");
        }

        return number;
    }

    private static bool ParseOnOff(string value)
    {
        if (value.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("LOCK", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("PRESS", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("UNLOCK", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidOperationException($"Expected ON/OFF payload. Received '{value}'.");
    }

    private static bool IsAcceptedCommandResponse(string body, List<string> checks)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var response = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var responseElement)
                ? responseElement
                : root;
            if (response.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            if (TryReadInt(response, "code", out var code))
            {
                checks.Add($"Tesla command response code: {code}.");
                return code is >= 200 and < 300;
            }

            if (TryReadBool(response, "result", out var result))
            {
                checks.Add($"Tesla command response result: {result}.");
                return result;
            }

            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), out value);
    }

    private static bool TryReadBool(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               bool.TryParse(property.GetString(), out value);
    }

    private static string TruncateForDiagnostics(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= 600
            ? value
            : $"{value[..600]}...";
}

sealed class TeslaFleetHomeAssistantCommandService(
    TeslaFleetStore store,
    TeslaFleetTokenCoordinator tokenCoordinator,
    TeslaFleetEnergyCommandClient commandClient,
    TeslaFleetVehicleCommandClient vehicleCommandClient,
    TeslaFleetDataClient dataClient,
    TeslaFleetMqttPublisher mqttPublisher,
    ILogger<TeslaFleetHomeAssistantCommandService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Home Assistant MQTT command listener failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var state = await store.LoadAsync(cancellationToken);
        if (!state.HomeAssistantMqttEnabled ||
            string.IsNullOrWhiteSpace(state.RefreshToken))
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            return;
        }

        var settings = TeslaMqttSettings.FromState(state);
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var clientId = BuildClientId();
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(settings.Host, settings.Port)
            .WithCleanSession();
        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            optionsBuilder = optionsBuilder.WithCredentials(settings.Username, settings.Password);
        }

        client.ApplicationMessageReceivedAsync += args => HandleCommandMessageAsync(settings, args, cancellationToken);

        await client.ConnectAsync(optionsBuilder.Build(), cancellationToken);
        var energyCommandFilter = $"{settings.BaseTopic}/energy/+/command/+";
        var vehicleCommandFilter = $"{settings.BaseTopic}/vehicles/+/command/+";
        await client.SubscribeAsync(
            new MqttClientSubscribeOptions
            {
                TopicFilters =
                [
                    new MqttTopicFilter
                    {
                        Topic = energyCommandFilter,
                        QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
                    },
                    new MqttTopicFilter
                    {
                        Topic = vehicleCommandFilter,
                        QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce
                    }
                ]
            },
            cancellationToken);
        logger.LogInformation(
            "Listening for Home Assistant MQTT commands on {EnergyCommandFilter} and {VehicleCommandFilter}.",
            energyCommandFilter,
            vehicleCommandFilter);

        var reconnectAfter = DateTimeOffset.UtcNow.AddMinutes(10);
        while (client.IsConnected &&
               DateTimeOffset.UtcNow < reconnectAfter &&
               !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        }

        if (client.IsConnected)
        {
            await client.DisconnectAsync(cancellationToken: cancellationToken);
        }
    }

    private async Task HandleCommandMessageAsync(
        TeslaMqttSettings settings,
        MqttApplicationMessageReceivedEventArgs args,
        CancellationToken cancellationToken)
    {
        var energyCommand = TryParseEnergyCommandTopic(settings, args.ApplicationMessage.Topic);
        var vehicleCommand = TryParseVehicleCommandTopic(settings, args.ApplicationMessage.Topic);
        if (energyCommand is null && vehicleCommand is null)
        {
            return;
        }

        args.IsHandled = true;
        var payload = ReadPayload(args.ApplicationMessage);
        var state = await store.LoadAsync(cancellationToken);
        TeslaFleetState updated;
        try
        {
            var token = await tokenCoordinator.EnsureUsableAsync(state, cancellationToken);
            if (energyCommand is not null)
            {
                var result = await commandClient.ExecuteAsync(
                    token.State,
                    energyCommand.Value.SiteId,
                    energyCommand.Value.Action,
                    payload,
                    cancellationToken);
                var checks = token.Checks.Concat(result.Checks).ToList();
                updated = token.State with
                {
                    LastStatus = result.Succeeded ? "Home Assistant command accepted" : "Home Assistant command failed",
                    LastMessage = result.Summary,
                    LastChecks = checks
                };

                if (result.Succeeded)
                {
                    var snapshot = await dataClient.FetchSnapshotAsync(updated, cancellationToken);
                    snapshot = ApplyEnergyCommandPatch(snapshot, result.SiteId, result.StatePatch);
                    var publishResult = await mqttPublisher.PublishAsync(updated, snapshot, cancellationToken);
                    updated = updated with
                    {
                        LastHomeAssistantPublishUtc = publishResult.Succeeded ? DateTimeOffset.UtcNow : updated.LastHomeAssistantPublishUtc,
                        LastHomeAssistantPublishSummary = publishResult.Summary,
                        LastHomeAssistantDiscoveryTopics = publishResult.Succeeded ? publishResult.DiscoveryTopics : updated.LastHomeAssistantDiscoveryTopics,
                        LastHomeAssistantStatePayloads = publishResult.Succeeded ? publishResult.StatePayloads : updated.LastHomeAssistantStatePayloads,
                        LastStatus = publishResult.Succeeded ? "Home Assistant command applied" : "Home Assistant command applied, publish failed",
                        LastMessage = publishResult.Summary,
                        LastChecks = checks.Concat(publishResult.Checks).ToList()
                    };
                }
            }
            else
            {
                var result = await vehicleCommandClient.ExecuteAsync(
                    token.State,
                    vehicleCommand!.Value.Vin,
                    vehicleCommand.Value.Action,
                    payload,
                    cancellationToken);
                var checks = token.Checks.Concat(result.Checks).ToList();
                updated = token.State with
                {
                    LastStatus = result.Succeeded ? "Home Assistant command accepted" : "Home Assistant command failed",
                    LastMessage = result.Summary,
                    LastChecks = checks
                };

                if (result.Succeeded)
                {
                    var snapshot = await dataClient.FetchSnapshotAsync(updated, cancellationToken);
                    snapshot = ApplyVehicleCommandPatch(snapshot, result.Vin, result.StatePatch);
                    var publishResult = await mqttPublisher.PublishAsync(updated, snapshot, cancellationToken);
                    updated = updated with
                    {
                        LastHomeAssistantPublishUtc = publishResult.Succeeded ? DateTimeOffset.UtcNow : updated.LastHomeAssistantPublishUtc,
                        LastHomeAssistantPublishSummary = publishResult.Summary,
                        LastHomeAssistantDiscoveryTopics = publishResult.Succeeded ? publishResult.DiscoveryTopics : updated.LastHomeAssistantDiscoveryTopics,
                        LastHomeAssistantStatePayloads = publishResult.Succeeded ? publishResult.StatePayloads : updated.LastHomeAssistantStatePayloads,
                        LastStatus = publishResult.Succeeded ? "Home Assistant command applied" : "Home Assistant command applied, publish failed",
                        LastMessage = publishResult.Summary,
                        LastChecks = checks.Concat(publishResult.Checks).ToList()
                    };
                }
            }
        }
        catch (Exception exception)
        {
            updated = state with
            {
                LastStatus = "Home Assistant command failed",
                LastMessage = exception.Message,
                LastChecks =
                [
                    $"Command topic: {args.ApplicationMessage.Topic}.",
                    $"Command payload: {payload}."
                ]
            };
            logger.LogWarning(exception, "Failed to process Home Assistant MQTT command {Topic}.", args.ApplicationMessage.Topic);
        }

        await store.SaveAsync(updated, cancellationToken);
    }

    private static TeslaFleetSnapshot ApplyEnergyCommandPatch(
        TeslaFleetSnapshot snapshot,
        string siteId,
        IReadOnlyDictionary<string, object?> patch)
    {
        if (string.IsNullOrWhiteSpace(siteId) || patch.Count == 0)
        {
            return snapshot;
        }

        var patched = false;
        var energySites = snapshot.EnergySites
            .Select(site =>
            {
                if (!site.SiteId.Equals(siteId, StringComparison.OrdinalIgnoreCase))
                {
                    return site;
                }

                var values = new Dictionary<string, object?>(site.Values, StringComparer.OrdinalIgnoreCase);
                foreach (var item in patch)
                {
                    values[item.Key] = item.Value;
                }

                patched = true;
                return site with { Values = values };
            })
            .ToList();
        if (!patched)
        {
            return snapshot;
        }

        var checks = snapshot.Checks.ToList();
        checks.Add($"Applied optimistic retained MQTT state patch for Energy site {siteId} after a successful command.");
        return snapshot with
        {
            EnergySites = energySites,
            Checks = checks
        };
    }

    private static TeslaFleetSnapshot ApplyVehicleCommandPatch(
        TeslaFleetSnapshot snapshot,
        string vin,
        IReadOnlyDictionary<string, object?> patch)
    {
        if (string.IsNullOrWhiteSpace(vin) || patch.Count == 0)
        {
            return snapshot;
        }

        var patched = false;
        var vehicles = snapshot.Vehicles
            .Select(vehicle =>
            {
                if (!vehicle.Vin.Equals(vin, StringComparison.OrdinalIgnoreCase))
                {
                    return vehicle;
                }

                var values = new Dictionary<string, object?>(vehicle.Values, StringComparer.OrdinalIgnoreCase);
                foreach (var item in patch)
                {
                    values[item.Key] = item.Value;
                }

                patched = true;
                return vehicle with { Values = values };
            })
            .ToList();
        if (!patched)
        {
            return snapshot;
        }

        var checks = snapshot.Checks.ToList();
        checks.Add($"Applied optimistic retained MQTT state patch for vehicle {vin} after a successful command.");
        return snapshot with
        {
            Vehicles = vehicles,
            Checks = checks
        };
    }

    private static (string SiteId, string Action)? TryParseEnergyCommandTopic(TeslaMqttSettings settings, string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return null;
        }

        var prefix = $"{settings.BaseTopic.TrimEnd('/')}/energy/";
        if (!topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = topic[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !parts[1].Equals("command", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return (parts[0], parts[2]);
    }

    private static (string Vin, string Action)? TryParseVehicleCommandTopic(TeslaMqttSettings settings, string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return null;
        }

        var prefix = $"{settings.BaseTopic.TrimEnd('/')}/vehicles/";
        if (!topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = topic[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !parts[1].Equals("command", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return (parts[0], parts[2]);
    }

    private static string ReadPayload(MqttApplicationMessage message)
    {
        var payload = message.Payload.ToArray();
        return payload.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(payload);
    }

    private static string BuildClientId()
    {
        var clientId = $"lms-tesla-fleet-command-{Environment.MachineName}-{Guid.NewGuid():N}";
        return clientId.Length <= 54 ? clientId : clientId[..54];
    }
}

sealed class TeslaFleetHomeAssistantPublisherService(
    TeslaFleetStore store,
    TeslaFleetTokenCoordinator tokenCoordinator,
    TeslaFleetDataClient dataClient,
    TeslaFleetMqttPublisher mqttPublisher,
    ILogger<TeslaFleetHomeAssistantPublisherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Home Assistant MQTT publish loop failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PublishIfDueAsync(CancellationToken cancellationToken)
    {
        var state = await store.LoadAsync(cancellationToken);
        if (!state.HomeAssistantMqttEnabled ||
            string.IsNullOrWhiteSpace(state.RefreshToken))
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(state.HomeAssistantRefreshIntervalMinutes, 5, 240));
        if (state.LastHomeAssistantPublishUtc.HasValue &&
            DateTimeOffset.UtcNow - state.LastHomeAssistantPublishUtc.Value < interval)
        {
            return;
        }

        var token = await tokenCoordinator.EnsureUsableAsync(state, cancellationToken);
        var snapshot = await dataClient.FetchSnapshotAsync(token.State, cancellationToken);
        var result = await mqttPublisher.PublishAsync(token.State, snapshot, cancellationToken);
        var updated = token.State with
        {
            LastHomeAssistantPublishUtc = result.Succeeded ? DateTimeOffset.UtcNow : token.State.LastHomeAssistantPublishUtc,
            LastHomeAssistantPublishSummary = result.Summary,
            LastHomeAssistantDiscoveryTopics = result.Succeeded ? result.DiscoveryTopics : token.State.LastHomeAssistantDiscoveryTopics,
            LastHomeAssistantStatePayloads = result.Succeeded ? result.StatePayloads : token.State.LastHomeAssistantStatePayloads,
            LastStatus = result.Succeeded ? "Home Assistant auto-published" : "Home Assistant auto-publish failed",
            LastMessage = result.Summary,
            LastChecks = token.Checks.Concat(result.Checks).ToList()
        };
        await store.SaveAsync(updated, cancellationToken);
    }
}

sealed record TeslaFleetSnapshot(
    DateTimeOffset CapturedUtc,
    JsonElement? User,
    JsonElement? Region,
    JsonElement? Products,
    List<TeslaVehicleSnapshot> Vehicles,
    List<TeslaEnergySiteSnapshot> EnergySites,
    List<string> Checks);

sealed record TeslaVehicleSnapshot(
    string Vin,
    string Id,
    string DisplayName,
    string State,
    IReadOnlyDictionary<string, object?> Values);

sealed record TeslaEnergySiteSnapshot(
    string SiteId,
    string DisplayName,
    string ResourceType,
    IReadOnlyDictionary<string, object?> Values);

sealed record TeslaMqttSettings(
    string Host,
    int Port,
    string Username,
    string Password,
    string DiscoveryPrefix,
    string BaseTopic)
{
    public static TeslaMqttSettings FromState(TeslaFleetState state) =>
        new(
            string.IsNullOrWhiteSpace(state.MqttHost) ? "core-mosquitto" : state.MqttHost.Trim(),
            state.MqttPort <= 0 ? 1883 : state.MqttPort,
            state.MqttUsername.Trim(),
            state.MqttPassword,
            NormalizeTopicRoot(state.MqttDiscoveryPrefix, "homeassistant"),
            NormalizeTopicRoot(state.MqttBaseTopic, "lms/tesla-fleet"));

    private static string NormalizeTopicRoot(string value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim().Trim('/');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}

sealed record TeslaHomeAssistantPublishResult(
    bool Succeeded,
    string Summary,
    List<string> Checks,
    List<string> DiscoveryTopics,
    List<HomeAssistantStatePayloadCacheEntry> StatePayloads);

sealed record TeslaEnergyCommand(
    string DisplayName,
    string Path,
    object Payload,
    IReadOnlyDictionary<string, object?> StatePatch);

sealed record TeslaEnergyCommandResult(
    bool Succeeded,
    string Summary,
    List<string> Checks,
    string SiteId,
    IReadOnlyDictionary<string, object?> StatePatch);

sealed record TeslaVehicleCommand(
    string DisplayName,
    string Path,
    object Payload,
    IReadOnlyDictionary<string, object?> StatePatch,
    bool UsesProxy);

sealed record TeslaVehicleCommandResult(
    bool Succeeded,
    string Summary,
    List<string> Checks,
    string Vin,
    IReadOnlyDictionary<string, object?> StatePatch);
