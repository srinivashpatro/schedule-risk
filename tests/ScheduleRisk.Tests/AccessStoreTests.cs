using ScheduleRisk.Web.Services.Access;

namespace ScheduleRisk.Tests;

/// <summary>
/// Sign-in for the Windows app: user accounts with hashed passwords, an optional login limit per account, and a choice
/// between a single account and a set number of accounts. Kept on the PC in one file.
/// </summary>
public class AccessStoreTests
{
    private sealed class MemoryFile : IAccessFile
    {
        public string? Text;
        public string? Read() => Text;
        public void Write(string text) => Text = text;
    }

    private static (AccessStore store, MemoryFile file) WithAdmin()
    {
        var file = new MemoryFile();
        var store = new AccessStore(file);
        store.CreateAdmin("admin", "admin-pass-1");
        return (store, file);
    }

    [Fact]
    public void A_new_install_asks_for_an_admin_account_first()
    {
        var store = new AccessStore(new MemoryFile());
        Assert.True(store.NeedsSetup);
        Assert.Equal(SignInResult.UnknownUser, store.SignIn("admin", "anything1"));
        store.CreateAdmin("admin", "admin-pass-1");
        Assert.False(store.NeedsSetup);
        Assert.Throws<InvalidOperationException>(() => store.CreateAdmin("other", "other-pass-1"));
    }

    [Fact]
    public void Passwords_are_stored_only_as_salted_hashes()
    {
        var (_, file) = WithAdmin();
        Assert.DoesNotContain("admin-pass-1", file.Text);
        Assert.Contains("\"Hash\"", file.Text);
        Assert.Contains("\"Salt\"", file.Text);
    }

    [Fact]
    public void Sign_in_checks_the_password_and_ignores_the_case_of_the_user_id()
    {
        var (store, _) = WithAdmin();
        Assert.Equal(SignInResult.WrongPassword, store.SignIn("admin", "nope-nope-1"));
        Assert.Null(store.CurrentUser);
        Assert.Equal(SignInResult.Ok, store.SignIn("ADMIN", "admin-pass-1"));
        Assert.Equal("admin", store.CurrentUser!.UserId);
        Assert.True(store.CurrentUser.IsAdmin);
        store.SignOut();
        Assert.Null(store.CurrentUser);
    }

    [Fact]
    public void Short_passwords_and_blank_or_repeated_user_ids_are_refused()
    {
        var (store, _) = WithAdmin();
        store.SetMode(singleUser: false, maxUsers: 5);
        Assert.Throws<ArgumentException>(() => store.AddUser("ann", "short", null));
        Assert.Throws<ArgumentException>(() => store.AddUser(" ", "long-enough-1", null));
        Assert.Throws<ArgumentException>(() => store.AddUser("Admin", "long-enough-1", null));
    }

    [Fact]
    public void A_login_limit_counts_each_sign_in_and_then_stops_the_account()
    {
        var (store, file) = WithAdmin();
        store.SetMode(singleUser: false, maxUsers: 3);
        store.AddUser("ann", "ann-pass-12", maxLogins: 2);
        Assert.Equal(SignInResult.Ok, store.SignIn("ann", "ann-pass-12"));
        Assert.Equal(1, store.Find("ann")!.LoginsLeft);
        Assert.Equal(SignInResult.Ok, store.SignIn("ann", "ann-pass-12"));
        Assert.Equal(SignInResult.LimitReached, store.SignIn("ann", "ann-pass-12"));
        Assert.Null(store.CurrentUser);

        // The count is saved, so restarting the app does not reset it.
        var reopened = new AccessStore(file);
        Assert.Equal(SignInResult.LimitReached, reopened.SignIn("ann", "ann-pass-12"));

        // The admin can give the account more logins.
        reopened.ResetLogins("ann");
        Assert.Equal(SignInResult.Ok, reopened.SignIn("ann", "ann-pass-12"));
    }

    [Fact]
    public void A_wrong_password_does_not_use_up_a_login()
    {
        var (store, _) = WithAdmin();
        store.SetMode(singleUser: false, maxUsers: 3);
        store.AddUser("ann", "ann-pass-12", maxLogins: 1);
        store.SignIn("ann", "wrong-pass-1");
        Assert.Equal(1, store.Find("ann")!.LoginsLeft);
    }

    [Fact]
    public void Accounts_without_a_limit_and_admin_accounts_never_run_out()
    {
        var (store, _) = WithAdmin();
        store.SetMode(singleUser: false, maxUsers: 3);
        store.AddUser("bob", "bob-pass-12", maxLogins: null);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(SignInResult.Ok, store.SignIn("bob", "bob-pass-12"));
            Assert.Equal(SignInResult.Ok, store.SignIn("admin", "admin-pass-1"));
        }
        Assert.Null(store.Find("bob")!.LoginsLeft);
    }

    [Fact]
    public void Single_user_mode_allows_only_the_admin_account()
    {
        var (store, _) = WithAdmin();
        Assert.True(store.Settings.SingleUser);
        Assert.Throws<InvalidOperationException>(() => store.AddUser("ann", "ann-pass-12", null));

        store.SetMode(singleUser: false, maxUsers: 2);
        store.AddUser("ann", "ann-pass-12", null);
        // Back to single user only once the other accounts are removed.
        Assert.Throws<InvalidOperationException>(() => store.SetMode(singleUser: true, maxUsers: 1));
        store.RemoveUser("ann");
        store.SetMode(singleUser: true, maxUsers: 1);
        Assert.True(store.Settings.SingleUser);
    }

    [Fact]
    public void Multi_user_mode_caps_the_number_of_accounts()
    {
        var (store, _) = WithAdmin();
        store.SetMode(singleUser: false, maxUsers: 3);
        store.AddUser("ann", "ann-pass-12", null);
        store.AddUser("bob", "bob-pass-12", null);
        Assert.Throws<InvalidOperationException>(() => store.AddUser("cat", "cat-pass-12", null));
        Assert.Throws<ArgumentException>(() => store.SetMode(singleUser: false, maxUsers: 2));
        Assert.Throws<ArgumentException>(() => store.SetMode(singleUser: false, maxUsers: 0));
    }

    [Fact]
    public void The_admin_account_cannot_be_removed_and_passwords_can_be_changed()
    {
        var (store, _) = WithAdmin();
        Assert.Throws<InvalidOperationException>(() => store.RemoveUser("admin"));
        store.SetPassword("admin", "new-admin-pass");
        Assert.Equal(SignInResult.WrongPassword, store.SignIn("admin", "admin-pass-1"));
        Assert.Equal(SignInResult.Ok, store.SignIn("admin", "new-admin-pass"));
    }

    [Fact]
    public void A_damaged_file_does_not_open_the_app()
    {
        var file = new MemoryFile { Text = "{ not json" };
        var store = new AccessStore(file);
        Assert.False(store.NeedsSetup);
        Assert.True(store.Damaged);
        Assert.Equal(SignInResult.UnknownUser, store.SignIn("admin", "admin-pass-1"));
    }
}
