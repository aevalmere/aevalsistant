import AppKit

// `Aevalsistant --hook [agent] [event]`: Claude Code passes no agent; Copilot CLI also passes
// the event. `Aevalsistant --status` prints what the running app knows. Anything else starts it.
let args = CommandLine.arguments
if args.count > 1 && args[1] == "--hook" {
    exit(HookClient.run(agent: args.count > 2 ? args[2] : "claude", event: args.count > 3 ? args[3] : ""))
}
if args.count > 1 && args[1] == "--status" {
    if let reply = Ipc.ask("{\"cmd\":\"status\"}") { print(reply, terminator: "") } else { print("Aevalsistant is not running") }
    exit(0)
}
Launcher.start()
