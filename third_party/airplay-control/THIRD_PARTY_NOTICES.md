# Third-party notices

go-atv itself is licensed under the GNU Lesser General Public License v3.0 (see
[`LICENSE`](LICENSE) and [`COPYING.GPL`](COPYING.GPL)). The material below comes
from other projects and keeps its own attribution.

## doubletake (LGPL-3.0-or-later)

Source: <https://github.com/omarroth/doubletake>, copyright (C) the doubletake
authors (principally Omar Roth). doubletake is licensed under the GNU Lesser
General Public License v3.0 or later. Its license text is in
[`third_party/doubletake/LICENSE`](third_party/doubletake/LICENSE) and the GPLv3
terms that the LGPL incorporates are in
[`third_party/doubletake/COPYING.GPL`](third_party/doubletake/COPYING.GPL).
doubletake states that releases v0.3.2 and earlier were provided under
GPL-3.0-or-later.

The following files are copied from doubletake's `internal/airplay/` package
and carry a header naming their origin. Files marked *modified* differ from
the original beyond the package name.

| go-atv file | doubletake file | Status |
| --- | --- | --- |
| `internal/fairplay/fairplay_crypto.go` | `fairplay_crypto.go` | modified |
| `internal/fairplay/fairplay_md5.go` | `fairplay_md5.go` | package name only |
| `internal/fairplay/fairplay_message.go` | `fairplay_message.go` | package name only |
| `internal/fairplay/fairplay_sap.go` | `fairplay_sap.go` | package name only |
| `internal/fairplay/fpsap.go` | `fpsap.go` | modified |
| `internal/fairplay/fpsap_tables.go` | `fpsap_tables.go` | package name only |
| `internal/fairplay/receiver_fairplay.go` | `receiver_fairplay.go` | modified |
| `internal/fairplay/fairplay_key_test.go` | `fairplay_key_test.go` | modified |
| `internal/fairplay/fairplay_vectors_test.go` | `fairplay_vectors_test.go` | package name only |
| `internal/fairplay/fpsap_test.go` | `fpsap_test.go` | package name only |
| `internal/fairplay/receiver_fairplay_test.go` | `receiver_fairplay_test.go` | package name only |
| `simulator/receiver_digest.go` | `receiver_digest.go` | modified |

Anyone receiving a go-atv binary or source that includes these files has the
rights and obligations the LGPL gives for them, including the right to modify
the covered files and to relink or rebuild the program against a modified
version. Keep this notice, the per-file headers and `third_party/doubletake/`
with any redistribution.

## pyatv (MIT)

The MRP protobuf subset and test fixtures in `internal/mrpwire/` come from
[postlund/pyatv](https://github.com/postlund/pyatv); see
[`internal/mrpwire/LICENSE.pyatv`](internal/mrpwire/LICENSE.pyatv). Much of the
protocol implementation elsewhere is a port of pyatv's behavior.

## Go module dependencies

The binaries link the modules below. Their license texts are saved under
[`third_party/licenses/`](third_party/licenses/) and ship in release archives,
as their licenses require.

| Module | License |
| --- | --- |
| `github.com/hashicorp/mdns` | MIT |
| `github.com/miekg/dns` | BSD-3-Clause |
| `golang.org/x/crypto`, `golang.org/x/net`, `golang.org/x/sys` | BSD-3-Clause |
| `google.golang.org/protobuf` | BSD-3-Clause |
| `howett.net/plist` | BSD-2-Clause |

Regenerate after changing dependencies, from the repository root:

```sh
go install github.com/google/go-licenses@v1.6.0
rm -rf third_party/licenses
go-licenses save ./cmd/goatv ./cmd/simulator --save_path third_party/licenses \
  --ignore github.com/jkiddo/go-atv
go-licenses check ./cmd/goatv ./cmd/simulator --ignore github.com/jkiddo/go-atv \
  --allowed_licenses=MIT,BSD-2-Clause,BSD-3-Clause,Apache-2.0,ISC
```

CI runs the `check` step, so a dependency under a license outside that list
fails the build.
