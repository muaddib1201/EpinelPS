# Restore official social recruit

> Implementation: execute the single change in this session using test-driven development and verification-before-completion.

**Goal:** Restore social point recruitment to the supplied client tables while retaining every owned character.

**Design:** Remove the friendship-only limited candidate replacement, SR additions and SSR rate boost in `GachaUtils`. Friendship also ignores account custom rarity rates, so its official grade and character weights remain authoritative. Keep custom rates on other eligible banners and keep active rate-up helpers used by the mileage shop. No save migration or character removal is required.

**Validation:** Use the session's static-data copy. Verify the official 2/43/55 rarity split, force every friendship grade and check exact list membership, sample default and custom-rate accounts, retain an owned Abnormal character byte-for-byte, and check other banners' custom rates. Run the existing gacha, selectup payment and duplicate character regressions.

- [x] Add `SocialGachaRegression` and observe the original limited-pool failure through the unified test entry.
- [x] Remove friendship overrides; rerun the targeted regressions (68 passing groups, including saved-state verification).
- [ ] Review the diff, integrate, sync, verify and push the exact integration commit to the authorized fork.
- [ ] Publish, seal and install through the unified deployment entry once the game has exited. Back up program and saves, verify roster preservation and health, and retain rollback.

The user authorized restoring official social recruitment and explicitly preserving existing characters. The implementation changes subsequent selections only.

Review: the independent read-only review found no selection or save-preservation blocker. Correct the simulator's displayed effective rates and command help to reflect the social exception; this is informational and keeps its output consistent with the selected probabilities. The original failure and passing logs remain in this session's ignored `.nikke/checks` directory.
