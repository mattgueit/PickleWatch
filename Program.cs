namespace PickleWatch;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

public class Program
{
    // Specific to local Pickleball group
    private const string Owner = "f13f25c2";
    private const int IntermediateAppointmentType = 74527079;

    private static readonly string ClientId = Environment.GetEnvironmentVariable("ACUITY_CLIENT_ID") ?? "";
    private static readonly string ClientSecret = Environment.GetEnvironmentVariable("ACUITY_CLIENT_SECRET") ?? "";
    private static readonly string Username = Environment.GetEnvironmentVariable("ACUITY_USERNAME") ?? "";
    private static readonly string Password = Environment.GetEnvironmentVariable("ACUITY_PASSWORD") ?? "";
    private static readonly string PushoverToken = Environment.GetEnvironmentVariable("PUSHOVER_TOKEN") ?? "";
    private static readonly string PushoverUser = Environment.GetEnvironmentVariable("PUSHOVER_USER") ?? "";
    private static readonly string NotifyDays = Environment.GetEnvironmentVariable("NOTIFY_DAYS") ?? "";
    
    public static async Task Main(string[] args)
    {
        var notifyDaysRaw = string.IsNullOrWhiteSpace(NotifyDays) ? "Monday" : NotifyDays;

        var notifyDays = notifyDaysRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Enum.Parse<DayOfWeek>(d, ignoreCase: true))
            .ToHashSet();
        
        using var http = new HttpClient();

        var accessToken = await GetAccessTokenAsync(http, ClientId, ClientSecret, Username, Password);

        var bookedAppointments = await GetBookedAppointmentsAsync(http, accessToken, Owner);
        var bookedKeys = bookedAppointments
            .Select(a => (a.AppointmentTypeId, Instant: DateTimeOffset.Parse(a.Datetime)))
            .ToHashSet();

        var availability = await GetAvailableClassesAsync(http, Owner);

        var openSlots = availability
            .SelectMany(kvp => kvp.Value)
            .Where(slot => slot.SlotsAvailable > 0)
            .Where(slot => !bookedKeys.Contains((slot.AppointmentTypeId, DateTimeOffset.Parse(slot.Time))) && slot.AppointmentTypeId == IntermediateAppointmentType && notifyDays.Contains(DateTime.Parse(slot.Time).DayOfWeek))
            .ToList();

        if (openSlots.Count > 0)
        {
            await SendPushoverNotificationAsync(http, PushoverToken, PushoverUser, openSlots);
            Console.WriteLine($"Notified: {openSlots.Count} open slot(s).");
        }
        else
        {
            Console.WriteLine("No open, unbooked slots.");
        }
    }
    
    static async Task<string> GetAccessTokenAsync(HttpClient http, string clientId, string clientSecret, string username, string password)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(clientId), "client_id");
        form.Add(new StringContent(clientSecret), "client_secret");
        form.Add(new StringContent("password"), "grant_type");
        form.Add(new StringContent("api-client-v1"), "scope");
        form.Add(new StringContent(username), "username");
        form.Add(new StringContent(password), "password");

        using var response = await http.PostAsync("https://app.acuityscheduling.com/oauth2/token", form);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("No access_token in response");
    }

    static async Task<List<BookedAppointment>> GetBookedAppointmentsAsync(HttpClient http, string accessToken, string owner)
    {
        // "now minus 20 minutes", formatted like 2026-07-23T17:01:50+1000 (no colon in offset)
        var startDate = DateTimeOffset.Now.AddMinutes(-20).ToString("yyyy-MM-ddTHH:mm:sszz00");

        var url = $"https://app.acuityscheduling.com/api/scheduling/v1/clients/appointments" +
                  $"?owner={owner}&startDate={Uri.EscapeDataString(startDate)}&timeSortOrder=asc&limit=10&offset=0";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<BookedAppointment>>() ?? new();
    }
    
    private static async Task<Dictionary<string, List<AvailabilitySlot>>> GetAvailableClassesAsync(
        HttpClient http, string owner)
    {
        var payload = new
        {
            owner,
            timezone = "Australia/Melbourne",
            limit = 15,
            offset = 0
        };

        using var response = await http.PostAsJsonAsync(
            "https://app.acuityscheduling.com/api/scheduling/v1/availability/class", payload);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<Dictionary<string, List<AvailabilitySlot>>>() ?? new();
    }


    private static async Task SendPushoverNotificationAsync(HttpClient http, string token, string user, List<AvailabilitySlot> slots)
    {
        string? message = null;
        foreach (var slot in slots)
        {
            if (!DateTime.TryParse(slot.Time, out var dateTime)) continue;
            
            if (message != null) { message += "\n"; }
            message += $"- {dateTime:dddd d MMMM}";
        }

        if (message == null) return;
        
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["user"] = user,
            ["message"] = $"Pickleball slot(s) opened:\n{message}"
        });

        using var response = await http.PostAsync("https://api.pushover.net/1/messages.json", form);
        response.EnsureSuccessStatusCode();
    }

    private record BookedAppointment(
        [property: JsonPropertyName("appointmentTypeId")] int AppointmentTypeId,
        [property: JsonPropertyName("datetime")] string Datetime,
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("type")] string Type
    );

    private record AvailabilitySlot(
        [property: JsonPropertyName("appointmentTypeId")] int AppointmentTypeId,
        [property: JsonPropertyName("calendarId")] int CalendarId,
        [property: JsonPropertyName("slotsAvailable")] int SlotsAvailable,
        [property: JsonPropertyName("time")] string Time
    );
}