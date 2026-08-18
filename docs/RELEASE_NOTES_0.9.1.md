# Snap Workspace 0.9.1

This maintenance release replaces the former polling-only background-window behavior with a PowerToys-inspired window lifecycle monitor.

- Background restoration now listens for native `EVENT_OBJECT_CREATE` and `EVENT_OBJECT_SHOW` notifications on a dedicated message-loop thread, based on the event-driven window discovery pattern used by PowerToys Workspaces.
- Matching windows are hidden immediately with `SW_HIDE`; the existing bounded scanner remains as a fallback for applications that suppress or delay accessibility events.
- Processes launched through command shells, bootstrap executables and other intermediaries are followed through their descendant process tree, so a differently named child window cannot escape the background rule.
- Everything keeps its native `-close` to `-startup` background protocol and is additionally protected by the generic event-driven guard.
- Cross-process presentation requests now use an asynchronous request plus a synchronous fallback, reducing races while a GUI thread creates its first window.
- Regression coverage includes existing windows, delayed windows, reused background processes, real Everything startup and a launcher that creates a differently named child process with a visible window.

PowerToys Workspaces itself launches desktop applications normally and supports restoring a saved minimized state; it does not provide a universal hidden-process launch switch. Snap Workspace reuses its window-event architecture while enforcing the stricter product meaning of a background application: no visible top-level window during workspace restoration.
