- Read graphify-out/GRAPH_REPORT.md before architecture questions or file searches.
- Use context7 for any library API you're not 100% sure about.
- Verify frontend changes with agent-browser before marking done.
- Prefer editing existing files over creating new ones. (Ponytail enforces the rest.)

## Browser Automation
Use `agent-browser` for web automation and to verify your own frontend work
before declaring a task done. Run `agent-browser --help` for commands.
Workflow: open <url> → snapshot -i (refs @e1, @e2) → click/fill → re-snapshot.
