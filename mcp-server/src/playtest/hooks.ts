// Playtest — how a project's playtest commands are recognised by name.
//
// The playbook names them `<owner>.playtest.begin` / `.end` / `.boot`; projects
// also write `playtest_begin` / `playtest_end` / `playtest_boot`. Both spellings
// count: begin = the session is isolated, end = it is not any more, boot =
// brings the game from its launcher to the first real screen.
export const BEGIN_HOOK = /(^|[._])playtest[._]begin$/;
export const END_HOOK = /(^|[._])playtest[._]end$/;
export const ISOLATION_HOOK = /(^|[._])playtest[._](begin|end)$/;
export const BOOT_COMMAND = /(^|[._])(playtest[._])?boot$/;
