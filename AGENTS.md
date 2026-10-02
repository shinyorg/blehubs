# Shiny.BluetoothLE.Hubs — Working Notes

Guidance for maintaining this repo. Shiny.BluetoothLE.Hubs provides **SignalR-style hubs over Bluetooth LE**, built on
Shiny.BluetoothLE (client) and Shiny.BluetoothLE.Hosting (host). It lives in its own repo, separate from the Shiny
core monorepo at `~/Desktop/dev/shiny`.

- Code lives in `src/`:
  - `Shiny.BluetoothLE.Hubs`: protocol, serializer, `[BleHubClient]`. It ships the generator in its package.
  - `Shiny.BluetoothLE.Hubs.SourceGenerators`
  - `Shiny.BluetoothLE.Hubs.Host`
  - `Shiny.BluetoothLE.Hubs.Client`
- Tests live in `tests/`, the MAUI sample in `samples/TicTacToe`, and the published Claude Code skill in
  `skills/shiny-ble-hubs`.
- **`PLAN.md` is the living design doc**: wire protocol, decisions, status and roadmap.

## Required updates for EVERY fix & feature

A change is not "done" until these are in sync:

1. **README.md** (repo root): reflect new or changed behavior. It is also the NuGet package readme.
2. **PLAN.md**: update the design sections, the decisions table and the status when behavior or a decision changes.
3. **Skill** (`skills/shiny-ble-hubs/SKILL.md` + `reference/api-reference.md`): the agent-facing "how to generate
   correct code" doc. Update the trigger keyword list when a new public API is introduced. The skill syncs to
   `shinyorg/skills` (plugin `shiny`) through `.github/workflows/sync-skills.yml` on pushes to main/v* that touch
   `skills/**`.
4. **Docs site** (`~/Desktop/dev/documentation`, Astro / Starlight, rendered to https://shinylib.net/blehubs/):
   - **Feature pages**: `src/content/docs/blehubs/*.mdx`:
     - `index` (Getting Started, setup, platforms)
     - `contracts` (`[BleHubClient]`, generated code, diagnostics)
     - `hosting` (`BleHub<T>`, `IHubContext`, groups, start/stop)
     - `client` (discovery, calls, events, failures)
     - `files` (L2CAP)
     - `how-it-works` (GATT layout, framing, limits, best practices)
   - **Release notes**: `src/content/docs/blehubs/release-notes.mdx`. This is its own file, not the Shiny client
     monorepo's shared notes, because this library ships from its own repo with its own version.
   - **Menu**: `src/sidebar-topics.mjs`, the **BluetoothLE Hubs** node under the *Hardware & Connectivity* topic, right
     after BluetoothLE Hosting. Add or update an item when you add a page.
   - **Homepage catalog**: the `BluetoothLE Hubs` entry in `src/data/libraryCatalog.ts` (tagline, summary,
     highlights, packages). The build fails if a sidebar library has no catalog entry, and its `label` must match the
     sidebar.
   - **Skills**: the skill is listed in `src/content/docs/foundation/ai-skills.mdx` and in the `skills` list in
     `src/components/AiSkill.astro`.
   - **Moving or renaming a page** needs a redirect in `astro.config.mjs`.
   - Run `npm run build` in the docs repo after changes, because MDX errors only surface at build time.
5. **Tests**: every behavior change gets a test in `tests/Shiny.BluetoothLE.Hubs.Tests` (see below).

### Release notes

Release notes live in `blehubs/release-notes.mdx` on the docs site:
- Use the `<RN>` component with `type="feature|enhancement|fix|chore"`, an optional `breaking` flag and an optional
  `platform`.
- Group under `## v<major>` → `### <version> - <date>`. Use `### <version> - TBD` for unreleased work.

**Never write a release note for a sample-app fix.** Release notes describe the shipped library. Changes confined to
`samples/` get no note.

## Architecture rules

- **AOT/trim safe, no reflection.** Dispatch, proxies and pushes are emitted by the source generator, and arguments
  are serialized one by one with their static type through `IBleHubSerializer`. Don't add `Activator`,
  `MethodInfo.Invoke` or reflection-based JSON. New public generics that DI constructs need
  `[DynamicallyAccessedMembers]`.
- **The libraries target `net10.0` only** and reference only the Shiny BLE abstractions. Platform stacks come from
  the app (`AddBluetoothLE()` / `AddBluetoothLeHosting()`). Don't add platform target frameworks unless a platform
  API is truly needed.
- **The wire protocol is versioned** (`FrameCodec.ProtocolVersion`, `FrameKind` values). Never renumber a
  `FrameKind`. A breaking wire change bumps the protocol version, and the handshake refuses mismatches. Hub
  methods and events travel **by name**, so renaming one is a breaking change for deployed clients.
- **Transport-free cores.**
  - `HubRuntime` (host) and `HubClientProtocol` / `BleHubClient.ConnectTransport` (client) don't know about BLE.
  - `BleHubHost` and `BleHubClient.Connect` are thin BLE adapters.
  - Keep logic in the cores so it stays testable without hardware.
- **Disconnect is cooperative.** iOS peripherals can't drop a central. Host-side disconnects send a `Disconnect`
  frame, and the client library leaves.
- **Ordering guarantees:**
  - The host sends whole messages to a peer under a lock.
  - Client pushes are raised one at a time, in the order received (through a channel).
  - A client is registered, and `OnConnectedAsync` has run, **before** the handshake ack goes out.
  - `Context.Abort()` inside a hub method takes effect after that method's reply is sent.
- **Per-hub start/stop.**
  - A GATT service holds every hub sharing its UUID. Stopped hubs stay in it but refuse handshakes.
  - A service is removed only when its last running hub stops.
  - Advertising lists exactly the services that have a running hub.

### Source generator

- `src/Shiny.BluetoothLE.Hubs.SourceGenerators`: netstandard2.0, incremental, with equatable models (`EquatableArray`, records
  only, no symbols in models). It is packed into `Shiny.BluetoothLE.Hubs`'s `analyzers/dotnet/cs`. Projects inside this repo
  reference it with `OutputItemType="Analyzer"`, because project references don't flow analyzers.
- It emits:
  - the client proxy, only when `Shiny.BluetoothLE.Hubs.Client` is referenced
  - push extensions, only when `Shiny.BluetoothLE.Hubs.Host` is referenced
  - a dispatcher plus the `IHubContext<THub>.Clients` C# 14 extension property per hub
  - `[ModuleInitializer]` registrations, which fall back to `RuntimeHelpers.RunModuleConstructor`
- New user-facing mistakes get an `SBH0xx` diagnostic plus a `GeneratorTests` case. Document new diagnostics in
  PLAN.md, the skill and the README.

## Tests

`dotnet test tests/Shiny.BluetoothLE.Hubs.Tests` must stay green. It runs without hardware:
- `FrameCodecTests` / `ArgumentCodecTests`: wire format.
- `HubTests`: the generated proxy against a real `HubRuntime` through an in-memory radio.
- `BleHubHostTests`: the real `BleHubHost` against `FakeHostingManager`, an in-memory GATT server, covering services,
  advertising, writes, notifications and start/stop.
- `GeneratorTests`: generator diagnostics via `CSharpGeneratorDriver`.

Run timing-sensitive tests a few times before calling them done (`--no-build` loop). They use `WaitFor` polling, not
fixed sleeps.

## Verifying on hardware

Unit tests and a simulator launch prove nothing about real radios. BLE needs **two physical devices**: the iOS
simulator has no Bluetooth and Android emulators are unreliable.

- Run the sample on both: `dotnet build samples/TicTacToe -f net10.0-android -t:Run` / `-f net10.0-ios -t:Run`.
- Exercise both directions: an iOS host with an Android client, and the reverse. Android host behavior, such as
  notification queuing, unsubscribe on drop and L2CAP on API 29+, differs from iOS.
- Things worth checking on every BLE-facing change:
  - discovery shows the host's local name
  - MTU negotiation (large payloads chunk correctly)
  - pushes keep their order
  - a host stop reaches the client's `Disconnected` with the reason
  - killing the client app makes the host reap it within `ClientSweepInterval`
  - avatar upload and download over L2CAP
- When something misbehaves, compare against a generic BLE tool (nRF Connect) to separate "the hardware/OS can't" from
  "the library doesn't".

## Build & release

- **Versioning**: Nerdbank.GitVersioning (`version.json`). `PublicRelease` is set on CI and tags `v*` produce release
  versions.
- **CI**: `.github/workflows/build.yml` runs on macOS. It builds `Build.slnf` (Release, which packs to `artifacts/`),
  runs the tests, and compile-checks the sample for Android and iOS. It publishes to NuGet through OIDC on pushes to
  `preview` / `v*`.
- **Skill sync**: `.github/workflows/sync-skills.yml` opens a PR against `shinyorg/skills` on branch
  `update-skills-ble-hubs`. It needs the `SKILLS_REPO_TOKEN` secret.

## Blog posts (only when explicitly requested)

Do **not** write blog posts automatically as part of a fix or feature. Write them **only when the user asks**. When
asked to blog a feature, produce **two** posts: first the docs-site version, then an adaptation for the personal blog.

### 1. Docs site — `~/Desktop/dev/documentation`

- File: `src/content/docs/blog/YYYY/MM/<slug>.mdx`, in the current year/month folders. Create the month folder if
  needed.
- Frontmatter:
  ```yaml
  ---
  title: '...'
  description: '...'
  date: YYYY-MM-DD
  authors:
    - allanritchie
  tags:
    - Release        # or Feature, AI, etc.
  ---
  ```
- The body is MDX. Reuse components where relevant, e.g. `import NugetBadge from '/src/components/NugetBadge.astro';`
  then `<NugetBadge name="Shiny.BluetoothLE.Hubs" />`.
- Voice: product/release-note tone covering what shipped, breaking changes, code samples and how to use it. **No hero
  image** on this site.

### 2. Personal blog — `~/Desktop/dev/blog` (adapt the docs post)

- File: `src/content/blog/YYYY/MM/<slug>.mdx`. Note the path is `content/blog`, not `content/docs/blog`.
- Frontmatter uses a different schema (see `src/content.config.ts`):
  ```yaml
  ---
  title: '...'
  description: '...'
  pubDate: 'Mon DD YYYY'                          # e.g. 'Jun 15 2026'
  heroImage: '../../../../assets/<slug>-hero.svg'
  tags: ['Shiny', '.NET MAUI']
  ---
  ```
- Voice: rework the docs post into a personal, first-person narrative ("Here's something that shouldn't be hard but
  is…", "So I built…"). Put the story and motivation up front, not a dry changelog.
- **A hero image is required.** Create `src/assets/<slug>-hero.svg`:
  - SVG, `viewBox="0 0 1200 630"`, `width="1200" height="630"`.
  - Match the house style: a dark navy/indigo gradient background (`#0f172a` → `#1e1b4b`), cyan/green/violet accent
    gradients, subtle glow filters, and the feature name as the headline. Use an existing one (e.g.
    `datasync-hero.svg`) as a starting template.
