## Summary

Describe the problem and the chosen solution.

## Validation

- [ ] `./scripts/verify_localization.ps1`
- [ ] `./build.ps1 -Configuration Release`
- [ ] Core protocol tests pass
- [ ] GUI behavior checked when applicable
- [ ] Real-device result includes ProductType/iOS but no UDID

## Risk review

- [ ] No real UDID, pairing record, user path, private screen content or raw USB capture is included
- [ ] No unbounded frame/audio queue or UI-thread USB work was introduced
- [ ] Driver, elevation and registry behavior is unchanged, or the change is clearly documented
- [ ] Protocol activation and stop behavior is unchanged, or tested on a real device
- [ ] Third-party binaries, hashes and license obligations are unchanged, or fully documented

## UI evidence

Attach sanitized screenshots for visual changes. Do not include personal phone content.
