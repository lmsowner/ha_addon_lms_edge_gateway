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

sealed class TeslaFleetMqttPublisher
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

        foreach (var vehicle in snapshot.Vehicles)
        {
            await PublishVehicleDiscoveryAsync(client, settings, vehicle, cancellationToken);
            await PublishJsonAsync(
                client,
                $"{settings.BaseTopic}/vehicles/{SafeTopic(vehicle.Vin)}/state",
                BuildVehicleState(vehicle, snapshot.CapturedUtc),
                retain: true,
                cancellationToken);
        }

        foreach (var site in snapshot.EnergySites)
        {
            await PublishEnergyDiscoveryAsync(client, settings, site, cancellationToken);
            await PublishJsonAsync(
                client,
                $"{settings.BaseTopic}/energy/{SafeTopic(site.SiteId)}/state",
                BuildEnergyState(site, snapshot.CapturedUtc),
                retain: true,
                cancellationToken);
        }

        await client.DisconnectAsync(cancellationToken: cancellationToken);
        checks.Add($"Published discovery/state for {snapshot.Vehicles.Count} vehicle(s) and {snapshot.EnergySites.Count} energy site(s).");
        checks.AddRange(snapshot.Checks);
        return new TeslaHomeAssistantPublishResult(
            true,
            $"Published {snapshot.Vehicles.Count} vehicle(s) and {snapshot.EnergySites.Count} energy site(s) to Home Assistant MQTT Discovery.",
            checks);
    }

    private static async Task PublishVehicleDiscoveryAsync(
        IMqttClient client,
        TeslaMqttSettings settings,
        TeslaVehicleSnapshot vehicle,
        CancellationToken cancellationToken)
    {
        var id = SafeId(vehicle.Vin);
        var stateTopic = $"{settings.BaseTopic}/vehicles/{SafeTopic(vehicle.Vin)}/state";
        var availabilityTopic = $"{settings.BaseTopic}/availability";
        var device = new
        {
            identifiers = new[] { $"lms_tesla_{id}" },
            name = vehicle.DisplayName,
            manufacturer = "Tesla",
            model = "Tesla Vehicle",
            sw_version = GetValue(vehicle.Values, "fleet_status.firmware_version")
        };
        var sensors = new[]
        {
            Sensor("state", "State", null, null, "{{ value_json.state }}"),
            Sensor("battery_level", "Battery", "battery", "%", "{{ value_json.battery_level }}"),
            Sensor("charging_state", "Charging State", null, null, "{{ value_json.charging_state }}"),
            Sensor("charge_limit_soc", "Charge Limit", "battery", "%", "{{ value_json.charge_limit_soc }}"),
            Sensor("plugged_in", "Plugged In", null, null, "{{ value_json.plugged_in }}"),
            Sensor("inside_temp", "Inside Temperature", "temperature", "\u00b0C", "{{ value_json.inside_temp }}"),
            Sensor("outside_temp", "Outside Temperature", "temperature", "\u00b0C", "{{ value_json.outside_temp }}"),
            Sensor("latitude", "Latitude", null, null, "{{ value_json.latitude }}"),
            Sensor("longitude", "Longitude", null, null, "{{ value_json.longitude }}"),
            Sensor("firmware_version", "Firmware", null, null, "{{ value_json.firmware_version }}"),
            Sensor("virtual_key_required", "Command Protocol Required", null, null, "{{ value_json.vehicle_command_protocol_required }}"),
            Sensor("total_keys", "Total Keys", null, null, "{{ value_json.total_number_of_keys }}")
        };

        foreach (var sensor in sensors)
        {
            var payload = new Dictionary<string, object?>
            {
                ["name"] = sensor.Name,
                ["unique_id"] = $"lms_tesla_{id}_{sensor.Id}",
                ["state_topic"] = stateTopic,
                ["value_template"] = sensor.ValueTemplate,
                ["availability_topic"] = availabilityTopic,
                ["device"] = device
            };
            if (!string.IsNullOrWhiteSpace(sensor.DeviceClass))
            {
                payload["device_class"] = sensor.DeviceClass;
            }

            if (!string.IsNullOrWhiteSpace(sensor.Unit))
            {
                payload["unit_of_measurement"] = sensor.Unit;
            }

            await PublishJsonAsync(
                client,
                $"{settings.DiscoveryPrefix}/sensor/lms_tesla_fleet/{id}_{sensor.Id}/config",
                payload,
                retain: true,
                cancellationToken);
        }
    }

    private static async Task PublishEnergyDiscoveryAsync(
        IMqttClient client,
        TeslaMqttSettings settings,
        TeslaEnergySiteSnapshot site,
        CancellationToken cancellationToken)
    {
        var id = SafeId(site.SiteId);
        var stateTopic = $"{settings.BaseTopic}/energy/{SafeTopic(site.SiteId)}/state";
        var availabilityTopic = $"{settings.BaseTopic}/availability";
        var device = new
        {
            identifiers = new[] { $"lms_tesla_energy_{id}" },
            name = site.DisplayName,
            manufacturer = "Tesla",
            model = "Tesla Energy Site"
        };
        var sensors = new[]
        {
            Sensor("grid_status", "Grid Status", null, null, "{{ value_json.grid_status }}"),
            Sensor("battery_percentage", "Battery", "battery", "%", "{{ value_json.battery_percentage }}"),
            Sensor("solar_power", "Solar Power", "power", "W", "{{ value_json.solar_power }}"),
            Sensor("load_power", "Load Power", "power", "W", "{{ value_json.load_power }}"),
            Sensor("battery_power", "Battery Power", "power", "W", "{{ value_json.battery_power }}"),
            Sensor("grid_power", "Grid Power", "power", "W", "{{ value_json.grid_power }}"),
            Sensor("backup_reserve_percent", "Backup Reserve", "battery", "%", "{{ value_json.backup_reserve_percent }}")
        };

        foreach (var sensor in sensors)
        {
            var payload = new Dictionary<string, object?>
            {
                ["name"] = sensor.Name,
                ["unique_id"] = $"lms_tesla_energy_{id}_{sensor.Id}",
                ["state_topic"] = stateTopic,
                ["value_template"] = sensor.ValueTemplate,
                ["availability_topic"] = availabilityTopic,
                ["device"] = device
            };
            if (!string.IsNullOrWhiteSpace(sensor.DeviceClass))
            {
                payload["device_class"] = sensor.DeviceClass;
            }

            if (!string.IsNullOrWhiteSpace(sensor.Unit))
            {
                payload["unit_of_measurement"] = sensor.Unit;
            }

            await PublishJsonAsync(
                client,
                $"{settings.DiscoveryPrefix}/sensor/lms_tesla_fleet/energy_{id}_{sensor.Id}/config",
                payload,
                retain: true,
                cancellationToken);
        }
    }

    private static Dictionary<string, object?> BuildVehicleState(TeslaVehicleSnapshot vehicle, DateTimeOffset capturedUtc) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["captured_utc"] = capturedUtc,
            ["vin"] = vehicle.Vin,
            ["display_name"] = vehicle.DisplayName,
            ["state"] = vehicle.State,
            ["battery_level"] = GetValue(vehicle.Values, "charge_state.battery_level"),
            ["charging_state"] = GetValue(vehicle.Values, "charge_state.charging_state"),
            ["charge_limit_soc"] = GetValue(vehicle.Values, "charge_state.charge_limit_soc"),
            ["plugged_in"] = GetValue(vehicle.Values, "charge_state.conn_charge_cable", "charge_state.fast_charger_present"),
            ["inside_temp"] = GetValue(vehicle.Values, "climate_state.inside_temp"),
            ["outside_temp"] = GetValue(vehicle.Values, "climate_state.outside_temp"),
            ["latitude"] = GetValue(vehicle.Values, "drive_state.latitude", "location_data.latitude"),
            ["longitude"] = GetValue(vehicle.Values, "drive_state.longitude", "location_data.longitude"),
            ["firmware_version"] = GetValue(vehicle.Values, "fleet_status.firmware_version"),
            ["vehicle_command_protocol_required"] = GetValue(vehicle.Values, "fleet_status.vehicle_command_protocol_required"),
            ["total_number_of_keys"] = GetValue(vehicle.Values, "fleet_status.total_number_of_keys")
        };

    private static Dictionary<string, object?> BuildEnergyState(TeslaEnergySiteSnapshot site, DateTimeOffset capturedUtc) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["captured_utc"] = capturedUtc,
            ["site_id"] = site.SiteId,
            ["display_name"] = site.DisplayName,
            ["resource_type"] = site.ResourceType,
            ["grid_status"] = GetValue(site.Values, "grid_status"),
            ["battery_percentage"] = GetValue(site.Values, "percentage_charged", "battery_percentage"),
            ["solar_power"] = GetValue(site.Values, "solar_power"),
            ["load_power"] = GetValue(site.Values, "load_power"),
            ["battery_power"] = GetValue(site.Values, "battery_power"),
            ["grid_power"] = GetValue(site.Values, "grid_power"),
            ["backup_reserve_percent"] = GetValue(site.Values, "backup_reserve_percent")
        };

    private static TeslaMqttSensor Sensor(string id, string name, string? deviceClass, string? unit, string valueTemplate) =>
        new(id, name, deviceClass, unit, valueTemplate);

    private static object? GetValue(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
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

sealed record TeslaMqttSensor(
    string Id,
    string Name,
    string? DeviceClass,
    string? Unit,
    string ValueTemplate);

sealed record TeslaHomeAssistantPublishResult(
    bool Succeeded,
    string Summary,
    List<string> Checks);
