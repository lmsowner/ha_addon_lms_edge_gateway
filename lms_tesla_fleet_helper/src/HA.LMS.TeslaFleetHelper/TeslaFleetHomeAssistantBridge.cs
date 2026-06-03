using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MQTTnet;
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

        if (state.FetchRealtimeVehicleData)
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
            updated.Add(realtime.HasValue
                ? vehicle with { Values = Merge(vehicle.Values, FlattenObject(ReadResponse(realtime.Value), null)) }
                : vehicle);
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
            var liveStatus = await TryGetResponseAsync(
                audience,
                $"/api/1/energy_sites/{Uri.EscapeDataString(site.SiteId)}/live_status",
                accessToken,
                checks,
                cancellationToken);
            updated.Add(liveStatus.HasValue
                ? site with { Values = Merge(site.Values, FlattenObject(ReadResponse(liveStatus.Value), null)) }
                : site);
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<TeslaHomeAssistantPublishResult> PublishAsync(
        TeslaFleetState state,
        TeslaFleetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!state.HomeAssistantMqttEnabled)
        {
            return new TeslaHomeAssistantPublishResult(false, "Home Assistant MQTT publishing is disabled.", []);
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

        await PublishStringAsync(client, $"{settings.BaseTopic}/availability", "online", retain: true, cancellationToken);
        var normalized = stateMapper.Map(snapshot, state.FleetApiAudience);
        var projection = projectionMapper.Map(normalized, settings.BaseTopic);
        var devices = projection.Devices.ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
        var discoveryTopics = new List<string>();
        foreach (var entity in projection.Entities)
        {
            if (!devices.TryGetValue(entity.DeviceId, out var device))
            {
                continue;
            }

            await PublishEntityDiscoveryAsync(client, settings, entity, device, cancellationToken);
            if (discoveryTopics.Count < 6)
            {
                discoveryTopics.Add(BuildDiscoveryTopic(settings, entity));
            }
        }

        foreach (var stateProjection in projection.States)
        {
            await PublishJsonAsync(client, stateProjection.Topic, stateProjection.Payload, retain: true, cancellationToken);
        }

        await client.DisconnectAsync(cancellationToken: cancellationToken);
        checks.Add($"Discovery prefix: {settings.DiscoveryPrefix}; base topic: {settings.BaseTopic}.");
        checks.Add($"Published {projection.Entities.Count} MQTT discovery config(s) for {projection.Devices.Count} device(s).");
        checks.Add($"Published {projection.States.Count} retained state topic(s).");
        if (discoveryTopics.Count > 0)
        {
            checks.Add($"Sample discovery topics: {string.Join(", ", discoveryTopics)}.");
        }
        if (projection.States.Count > 0)
        {
            checks.Add($"State topics: {string.Join(", ", projection.States.Select(topic => topic.Topic).Take(6))}.");
        }
        checks.Add($"Snapshot source contained {snapshot.Vehicles.Count} vehicle(s) and {snapshot.EnergySites.Count} energy site(s).");
        checks.AddRange(snapshot.Checks);
        return new TeslaHomeAssistantPublishResult(
            true,
            $"Published {projection.Entities.Count} Home Assistant MQTT Discovery config(s) from typed LMS Tesla projection.",
            checks);
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
        var payload = new Dictionary<string, object?>
        {
            ["name"] = entity.Name,
            ["unique_id"] = entity.Id,
            ["state_topic"] = entity.StateTopic,
            ["value_template"] = entity.ValueTemplate,
            ["availability_topic"] = $"{settings.BaseTopic}/availability",
            ["device"] = devicePayload
        };
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

    private static string BuildDiscoveryTopic(TeslaMqttSettings settings, HomeAssistantMqttEntityProjection entity) =>
        $"{settings.DiscoveryPrefix}/{entity.Component}/lms_tesla_fleet/{entity.Id}/config";

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
    List<string> Checks);
