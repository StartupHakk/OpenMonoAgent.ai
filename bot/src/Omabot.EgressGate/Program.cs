// SPDX-License-Identifier: AGPL-3.0-or-later
// The egress gate is built in phase 7. Until then it refuses to run, so no
// bot can be pointed at a proxy that does not enforce the address policy.
Console.Error.WriteLine("oma-bot-egress: not built yet (phase 7).");
return 1;
