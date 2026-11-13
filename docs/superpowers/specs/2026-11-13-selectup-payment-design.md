# Select-up recruitment payment

The user authorized fixing banner 10094 after isolated requests reproduced rejection of currency 5503 for every choice, including Grave (10503 / 451401). Production uses the same payment code as the reproduction.

The existing recruitment transaction remains the owner of character grants, payment, counters and rollback. Extend its ticket pipeline to accept the configured select-up ticket (5501/5502/5503), consume it first, then the configured advanced recruit ticket, then free and paid gems. Explicit gem requests retain their existing behavior. The selected ticket must belong to the banner and prices must be positive; unsupported discounts must not create free recruitment. Current ticket prices are linear: one ticket per draw and ten per ten draws.

Alternatives considered: special-casing 5503 alone would repeat the same omission for other select-up tickets; replacing payment for every banner would unnecessarily change standard/social recruitment. Use the existing pipeline with a narrowly scoped select-up extension. Do not change character probabilities or the saved selection schema.

Verify all three choices through the actual selection and execution handlers with synthetic accounts. Cover single/ten pulls, ticket/mixed/gem payments, exhaustion and concurrent attempts, response balances, persistent save, mission progress and rollback. No live pulls or account edits. Use the registered session workflow for integration, tests and release; back up the verified integration to the authorized GitHub fork. Prepare and seal the release before requesting permission for any live stop/install/restart.
