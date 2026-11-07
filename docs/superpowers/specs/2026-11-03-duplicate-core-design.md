# Duplicate character and spare-body correction

Scope: fix duplicate characters/spare bodies only. The user explicitly deferred Coordinated Operation; Broken Cores and Rusted Bones are not Body Labels and must not be minted here.

Rules: SSR has 10 upgrades (3 limit breaks + core 7), SR has 2, R has 0. Reserve only the spare bodies still usable, including existing inventory; convert excess to 6000/200/150 Body Labels respectively. Use the owned character's table GradeCoreId, not the potentially inconsistent saved Grade. An unowned character's directly awarded spare body never unlocks the character. Batch rewards must grant every copy, and client reward deltas/final balances must agree with saved state.

Approach: centralize character and spare-body grants, route normal/daily/pity gacha, molds, generic rewards and both shop purchase paths through it. A login reconciliation preserves usable and unowned spare bodies and converts only existing excess, once. Preserve unrelated local changes and acquisition/messenger triggers. Do not claw back historical currency credits.

Alternatives considered: fixing each duplicated condition separately would retain diverging grant and response behavior; a common grant implementation also fixes lost mold stacks, batch rewards, and double crediting. No client changes or new currency design are needed.

Verification: isolated real-table/SQLite fixtures cover caps, stored stock, batches, fresh ownership, every grant endpoint, shop spending, persistence, and idempotent reconciliation. Existing regression suites must pass. Back up current executable and saves before deployment; restart and check server readiness. In-game acceptance remains separate.

Reference for the published duplicate compensation amounts: https://nikke.gg/shop-guide/ (checked 2026-11-03). Official website articles require JavaScript and did not expose their body to the web reader; this limitation must not be represented as a verified official quotation. The local shipped character table supplies actual upgrade caps.
