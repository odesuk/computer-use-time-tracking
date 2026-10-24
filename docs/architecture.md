# Architecture and downstream use

`src/Core` is the platform-neutral tracking, SQLite, settings, retention, and reporting implementation. `src/Windows` adds Win32 activity sampling, Outlook providers, WPF settings, the tray host, startup registration, and Windows packaging behavior.

The Windows host exposes one compile-time composition point: `TrackerApplication.Run` accepts a product identity and an optional service-registration delegate. The standalone entrypoint passes the checked-in local-only identity and no extra services. It does not scan directories, load plugins, or discover integrations from settings.

A private or branded downstream product should consume a pinned commit of this repository, normally as a Git submodule. Its small entrypoint supplies its identity and explicitly registers its own services. Private authentication, synchronization, and automation code stays in the downstream repository. Changes to tracking behavior are made here once, committed, then adopted by advancing the downstream submodule pointer.

The standalone app deliberately uses its own data directory, report directory, startup task, and single-instance names. A downstream host must provide its existing identity when compatibility with installed users matters; silently sharing or renaming these values can expose, orphan, or double-count activity.

Unknown properties in `client-settings.json` are retained through load/save. This lets an explicitly compiled downstream host preserve its own settings without placing those fields or their behavior in the public model.
