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
// Commands an agent runs from its shell. Each prints what the app did and exits 1 when the app
// refused, 2 when the command is wrong or the app is not running.
let agentCommands: Set<String> = ["--awake", "--sleep", "--restart", "--shutdown", "--notify"]
if args.dropFirst().contains(where: { agentCommands.contains($0) }) {
    guard let req = AgentRequest.parse(Array(args.dropFirst())) else {
        let usage = """
        Usage:
          Aevalsistant --awake <30m|2h|1h30m|on> ["reason"]   keep the computer awake
          Aevalsistant --awake off                            release it
          Aevalsistant --sleep | --restart | --shutdown       when nothing else is busy
          Aevalsistant --notify "title" ["detail"] [--needs-you]
          Aevalsistant --status

        """
        FileHandle.standardError.write(usage.data(using: .utf8) ?? Data())
        exit(2)
    }
    let found = Terminals.fromHookProcess(req.agent)
    req.pwd = FileManager.default.currentDirectoryPath
    req.host = found.host
    req.appPid = found.appPid
    req.pid = found.agentPid
    req.pidStart = found.agentStart
    guard let reply = Ipc.ask(req.json()) else {
        print("Aevalsistant is not running")
        exit(2)
    }
    print(reply, terminator: reply.hasSuffix("\n") ? "" : "\n")
    exit(reply.hasPrefix("Refused") || reply.hasPrefix("Not shown") ? 1 : 0)
}
Launcher.start()
