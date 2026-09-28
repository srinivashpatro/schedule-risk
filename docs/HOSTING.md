# Hosting the browser app

The browser app is a static site: every file it needs is published once, and the whole engine runs in the
visitor's browser. Schedules are opened there and never uploaded, wherever the site is hosted.

The workflow ([.github/workflows/build-and-deploy.yml](../.github/workflows/build-and-deploy.yml)) builds the
site on every push to `main` once the build and tests pass, and publishes two copies of it:

| Copy | Address | Who can open it |
|---|---|---|
| GitHub Pages | <https://srinivashpatro.github.io/schedule-risk/> | Anyone (public) |
| Cloudflare Pages behind Cloudflare Access | `https://<project>.pages.dev/` | Only invited email addresses |

Both copies carry the current app plus the earlier releases in `v0.4/` and `v0.3/`. They differ only in their
`<base href>`: GitHub Pages serves the site under `/schedule-risk/`, and Cloudflare Pages serves it at the root
of its address.

The GitHub Pages copy stays public for now, and so does the repository. Anyone can use that copy or build the
app from source, so Cloudflare Access controls who can open the Cloudflare copy, not who can use the app.

## How Cloudflare Access protects the Cloudflare copy

Cloudflare checks every request, for every file, before it serves anything. A visitor without a valid session
gets Cloudflare's sign-in page instead of the app: they enter their email address, and if it is on the invited
list, Cloudflare emails them a one-time PIN. Signing in lasts 48 hours, after which Cloudflare asks again.

The app has no sign-in code of its own. Cloudflare's sign-in page and its `CF_Authorization` cookie sit
outside the app, so the privacy promise is unchanged:

- Cloudflare serves the app's own files and learns who signed in and when. It never sees a schedule, a risk
  model or a result: those stay in the browser.
- The invited email addresses live in Cloudflare's settings, never in this repository, which is public.
- Leave Cloudflare Pages' Web Analytics off. It adds a script from another site to every page, which the app
  promises not to load (`WebAssetsTests`).

If a session ends while the app is open, the files the app fetches later (the report fonts on the first
export, the sample project) fail to load. The app then says to open it in a new tab, sign in there, and try
again in the first tab, which keeps its work. The sign-in is shared between the tabs.

## One-time setup

Cloudflare's dashboard moves things around from time to time. If a label below has changed, Cloudflare's docs
for Pages and Access describe the same steps.

1. **Cloudflare account and Zero Trust.** Sign up at Cloudflare, then open Zero Trust from the dashboard. The
   first time, choose a team name (it becomes `<team>.cloudflareaccess.com`, the sign-in address) and the
   Free plan, which covers up to 50 users.
2. **API token.** Go to My Profile, then API Tokens, then Create Token, then Custom token. Give it one
   permission: Account, Cloudflare Pages, Edit. Copy the token.
3. **Pages project.** In a terminal, run
   `CLOUDFLARE_API_TOKEN=<token> CLOUDFLARE_ACCOUNT_ID=<account id> npx wrangler@4 pages project create <project> --production-branch=main`.
   The account ID is shown on the dashboard's Workers & Pages page. The command prints the project's
   address, `https://<project>.pages.dev/`; Cloudflare adds a suffix to the address if the name is taken.
4. **Repository settings.** In GitHub, go to Settings, then Secrets and variables, then Actions, and add:
   - Secret `CLOUDFLARE_API_TOKEN`: the token from step 2.
   - Variable `CLOUDFLARE_ACCOUNT_ID`: the account ID.
   - Variable `CLOUDFLARE_PAGES_PROJECT`: the project name from step 3.

   The workflow's "Deploy to Cloudflare Pages" step is skipped until `CLOUDFLARE_PAGES_PROJECT` is set. If a
   Cloudflare deploy fails, the whole run fails, so the GitHub Pages copy is not updated either.
5. **One-time PIN.** In Zero Trust, go to Settings, then Authentication, then Login methods, then Add new,
   then One-time PIN.
6. **Protect every address before the first deploy.** In the Pages project, go to Settings, then General,
   then Access policy, then Enable. This creates an Access application for `*.<project>.pages.dev`: the
   address each deployment gets and any preview address. In Zero Trust, go to Access, then Applications, and
   edit that application:
   - Add the production address `<project>.pages.dev` to it, next to `*.<project>.pages.dev`. Both must be
     covered, or one of them serves the app without a sign-in.
   - Session duration: 48 hours. If the list has no 48-hour choice, use a custom duration, or set
     `session_duration` to `48h` through Cloudflare's API.
   - Login methods: One-time PIN only. With a single method, Instant Auth skips the choice screen.
   - Policy: action Allow, with a rule to Include the Emails of the invited people. Remove any rule the
     dashboard added by default that lets in more people than those.
7. **First deploy.** Push to `main`, or run the workflow by hand from the Actions tab.
8. **Check it from a private browser window** before you share the address:
   - Signed out, each of these must show Cloudflare's sign-in page and no app file:
     `/`, `/index.html`, `/_framework/blazor.boot.json`, `/sample/sample-project.xer`,
     `/fonts/archivo-doc-400.ttf`, `/v0.4/` and `/v0.3/`, plus one deployment address
     (`<hash>.<project>.pages.dev`, listed under the project's Deployments).
   - An address that is not invited never receives a PIN.
   - Signed in with an invited address, the app loads. Then open the sample project, run a simulation, and
     export every report format (PDF, Word, PowerPoint, HTML, CSV).

## Inviting and removing people

To invite someone, add their email address to the application's policy. To remove someone, take their
address off the policy and also revoke their session: in Zero Trust, go to My Team, then Users, pick the
person, and choose Revoke session. Otherwise their current sign-in keeps working for up to 48 hours.
