using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScheduleRisk.Web.Services.Access;

/// <summary>Where the accounts are kept: one text file on the PC in the Windows app, memory in the tests.</summary>
public interface IAccessFile
{
    string? Read();
    void Write(string text);
}

public enum SignInResult { Ok, UnknownUser, WrongPassword, LimitReached }

public sealed class AccessUser
{
    public string UserId { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Iterations { get; set; }
    public bool IsAdmin { get; set; }
    /// <summary>How many times the account may sign in; null for no limit. Admin accounts have no limit.</summary>
    public int? MaxLogins { get; set; }
    public int LoginCount { get; set; }
    public DateTime? LastLogin { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int? LoginsLeft => IsAdmin || MaxLogins is not { } max ? null : Math.Max(0, max - LoginCount);
}

public sealed class AccessSettings
{
    /// <summary>Single user: only the admin account. Otherwise up to <see cref="MaxUsers"/> accounts, the admin included.</summary>
    public bool SingleUser { get; set; } = true;
    public int MaxUsers { get; set; } = 1;
    public List<AccessUser> Users { get; set; } = new();
}

/// <summary>
/// Sign-in for the Windows app. Passwords are kept only as salted PBKDF2 hashes. The file lives on the user's PC, so
/// this keeps casual users out but cannot stop someone who can edit or delete that file: it is a lock, not a safe.
/// Nothing here touches the network.
/// </summary>
[UnsupportedOSPlatform("browser")]
public sealed class AccessStore
{
    public const int MinPasswordLength = 8;
    private const int Iterations = 210_000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IAccessFile file;

    public AccessStore(IAccessFile file)
    {
        this.file = file;
        string? text = file.Read();
        if (string.IsNullOrWhiteSpace(text)) Settings = new AccessSettings();
        else
        {
            try { Settings = JsonSerializer.Deserialize<AccessSettings>(text) ?? throw new JsonException(); }
            catch (JsonException) { Settings = new AccessSettings(); Damaged = true; }
        }
    }

    public AccessSettings Settings { get; private set; }

    /// <summary>The file could not be read. Nobody can sign in, and setup is not offered, until it is fixed or deleted.</summary>
    public bool Damaged { get; }

    public bool NeedsSetup => !Damaged && Settings.Users.Count == 0;

    public AccessUser? CurrentUser { get; private set; }

    public event Action? Changed;

    public AccessUser? Find(string userId) =>
        Settings.Users.FirstOrDefault(u => string.Equals(u.UserId, userId.Trim(), StringComparison.OrdinalIgnoreCase));

    public void CreateAdmin(string userId, string password)
    {
        if (!NeedsSetup) throw new InvalidOperationException("The admin account has already been set up.");
        Settings.Users.Add(NewUser(userId, password, isAdmin: true, maxLogins: null));
        Save();
    }

    public SignInResult SignIn(string userId, string password)
    {
        CurrentUser = null;
        if (Damaged || Find(userId) is not { } user) return SignInResult.UnknownUser;
        if (!Matches(user, password)) return SignInResult.WrongPassword;
        if (user.LoginsLeft == 0) return SignInResult.LimitReached;
        user.LoginCount++;
        user.LastLogin = DateTime.Now;
        Save();
        CurrentUser = user;
        Changed?.Invoke();
        return SignInResult.Ok;
    }

    public void SignOut()
    {
        CurrentUser = null;
        Changed?.Invoke();
    }

    public void SetMode(bool singleUser, int maxUsers)
    {
        if (singleUser)
        {
            if (Settings.Users.Count > 1)
                throw new InvalidOperationException("Remove the other accounts before switching to single user.");
            maxUsers = 1;
        }
        else if (maxUsers < 1 || maxUsers < Settings.Users.Count)
            throw new ArgumentException($"The number of accounts must be at least {Math.Max(1, Settings.Users.Count)}.");
        Settings.SingleUser = singleUser;
        Settings.MaxUsers = maxUsers;
        Save();
    }

    public void AddUser(string userId, string password, int? maxLogins)
    {
        if (Settings.SingleUser) throw new InvalidOperationException("Single user mode allows only the admin account.");
        if (Settings.Users.Count >= Settings.MaxUsers)
            throw new InvalidOperationException($"The limit of {Settings.MaxUsers} accounts has been reached.");
        Settings.Users.Add(NewUser(userId, password, isAdmin: false, maxLogins));
        Save();
    }

    public void RemoveUser(string userId)
    {
        var user = Find(userId) ?? throw new ArgumentException("No such account.");
        if (user.IsAdmin) throw new InvalidOperationException("The admin account cannot be removed.");
        Settings.Users.Remove(user);
        Save();
    }

    public void SetPassword(string userId, string password)
    {
        var user = Find(userId) ?? throw new ArgumentException("No such account.");
        CheckPassword(password);
        SetHash(user, password);
        Save();
    }

    public void SetLoginLimit(string userId, int? maxLogins)
    {
        var user = Find(userId) ?? throw new ArgumentException("No such account.");
        user.MaxLogins = CheckLimit(maxLogins);
        Save();
    }

    public void ResetLogins(string userId)
    {
        var user = Find(userId) ?? throw new ArgumentException("No such account.");
        user.LoginCount = 0;
        Save();
    }

    private AccessUser NewUser(string userId, string password, bool isAdmin, int? maxLogins)
    {
        userId = userId.Trim();
        if (userId.Length == 0) throw new ArgumentException("Enter a user ID.");
        if (Find(userId) != null) throw new ArgumentException($"There is already an account called {userId}.");
        CheckPassword(password);
        var user = new AccessUser { UserId = userId, IsAdmin = isAdmin, MaxLogins = isAdmin ? null : CheckLimit(maxLogins) };
        SetHash(user, password);
        return user;
    }

    private static void CheckPassword(string password)
    {
        if (password.Length < MinPasswordLength)
            throw new ArgumentException($"The password must be at least {MinPasswordLength} characters.");
    }

    private static int? CheckLimit(int? maxLogins) =>
        maxLogins is < 1 ? throw new ArgumentException("A login limit must be 1 or more, or blank for no limit.") : maxLogins;

    private static void SetHash(AccessUser user, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        user.Salt = Convert.ToBase64String(salt);
        user.Iterations = Iterations;
        user.Hash = Convert.ToBase64String(Derive(password, salt, Iterations));
    }

    private static bool Matches(AccessUser user, string password)
    {
        try
        {
            byte[] expected = Convert.FromBase64String(user.Hash);
            byte[] actual = Derive(password, Convert.FromBase64String(user.Salt), user.Iterations);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Math.Max(1, iterations), HashAlgorithmName.SHA256, 32);

    private void Save()
    {
        file.Write(JsonSerializer.Serialize(Settings, Json));
        Changed?.Invoke();
    }
}
