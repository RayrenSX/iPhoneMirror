#include "Renderer/OutputModeState.h"

#include <cstddef>
#include <initializer_list>
#include <iostream>
#include <string_view>
#include <utility>
#include <vector>

namespace {

using namespace iPhoneMirror::renderer::output;

int failures{};

void check(bool condition, std::string_view message) {
    if (condition) return;
    ++failures;
    std::cerr << "FAIL: " << message << '\n';
}

enum class Call {
    ReleaseTargets,
    ResizeSdr,
    ResizeHdr,
    ColorSdr,
    ColorHdr,
    CreateTarget,
};

struct FakeBackend {
    std::vector<Failure> resize_results;
    std::vector<Failure> color_results;
    std::vector<Failure> target_results;
    std::vector<Call> calls;
    std::size_t resize_index{};
    std::size_t color_index{};
    std::size_t target_index{};

    void release_targets() noexcept { calls.push_back(Call::ReleaseTargets); }

    Failure resize(SurfaceFormat format) noexcept {
        calls.push_back(format == SurfaceFormat::Rgba16Float
            ? Call::ResizeHdr : Call::ResizeSdr);
        return next(resize_results, resize_index);
    }

    Failure set_color_space(Mode mode) noexcept {
        calls.push_back(mode == Mode::Hdr ? Call::ColorHdr : Call::ColorSdr);
        return next(color_results, color_index);
    }

    Failure create_target() noexcept {
        calls.push_back(Call::CreateTarget);
        return next(target_results, target_index);
    }

private:
    static Failure next(const std::vector<Failure>& values, std::size_t& index) noexcept {
        return index < values.size() ? values[index++] : Failure::None;
    }
};

void check_calls(const FakeBackend& backend, std::initializer_list<Call> expected,
    std::string_view message) {
    check(backend.calls == std::vector<Call>(expected), message);
}

State hdr_state(std::uint64_t monitor_generation = 1) {
    State state;
    state.actual_format = SurfaceFormat::Rgba16Float;
    state.applied_color_space = AppliedColorSpace::Hdr;
    state.target_valid = true;
    state.desired_mode = Mode::Hdr;
    state.monitor_generation = monitor_generation;
    state.color_space_monitor_generation = monitor_generation;
    return state;
}

void test_successful_sdr_to_hdr_switch() {
    State state;
    const auto plan = plan_update(state, Mode::Hdr, 0, 0);
    check(plan.action == Action::ExecuteTransaction,
        "SDR to HDR plans a transaction");
    FakeBackend backend;
    const auto result = execute_transaction(state, backend, 0);
    check(result.applied && result.needs_redraw && !result.rebuild_device,
        "successful HDR switch applies and requests redraw");
    check(state.actual_format == SurfaceFormat::Rgba16Float &&
        state.applied_color_space == AppliedColorSpace::Hdr &&
        state.target_valid && state.desired_is_applied(),
        "successful HDR switch commits format, color space, and target atomically");
    check_calls(backend, {Call::ReleaseTargets, Call::ResizeHdr,
        Call::ColorHdr, Call::CreateTarget},
        "successful HDR switch uses the expected backend sequence");
}

void test_hdr_effective_requires_source_monitor_and_surface() {
    for (unsigned combination{}; combination < 8; ++combination) {
        const bool source_hdr = (combination & 1U) != 0;
        const bool monitor_hdr = (combination & 2U) != 0;
        const bool actual_hdr_surface = (combination & 4U) != 0;
        check(hdr_output_is_effective(
            source_hdr, monitor_hdr, actual_hdr_surface) == (combination == 7U),
            "HDR is effective only when source, monitor, and surface are HDR");
    }
}

void test_resize_failure_preserves_previous_state_and_backs_off() {
    State state;
    (void)plan_update(state, Mode::Hdr, 0, 0);
    FakeBackend backend{.resize_results = {Failure::Transient}};
    const auto result = execute_transaction(state, backend, 0);
    check(!result.applied && !result.needs_redraw &&
        result.failure == Failure::Transient,
        "failed resize is transient without discarding contents");
    check(state.actual_format == SurfaceFormat::Bgra8 &&
        state.applied_color_space == AppliedColorSpace::Sdr && state.target_valid,
        "failed resize restores the released target on the previous output");
    check(plan_update(state, Mode::Hdr, 0, 249).action == Action::None &&
        plan_update(state, Mode::Hdr, 0, 250).action == Action::ExecuteTransaction,
        "first transient failure waits 250ms before retry");
    check_calls(backend, {Call::ReleaseTargets, Call::ResizeHdr, Call::CreateTarget},
        "resize failure does not attempt a color-space change");
}

void test_color_failure_rolls_back_completely_and_blocks_unsupported() {
    State state;
    (void)plan_update(state, Mode::Hdr, 4, 0);
    FakeBackend backend{
        .resize_results = {Failure::None, Failure::None},
        .color_results = {Failure::Unsupported, Failure::None},
    };
    const auto result = execute_transaction(state, backend, 0);
    check(!result.applied && result.needs_redraw &&
        result.failure == Failure::Unsupported,
        "unsupported HDR color space reports a redraw-producing rollback");
    check(state.actual_format == SurfaceFormat::Bgra8 &&
        state.applied_color_space == AppliedColorSpace::Sdr && state.target_valid &&
        state.color_space_monitor_generation == 4,
        "complete rollback restores a validated SDR output");
    check(plan_update(state, Mode::Hdr, 4, 100'000).action == Action::None,
        "unsupported output remains blocked on the same monitor generation");
    check_calls(backend, {Call::ReleaseTargets, Call::ResizeHdr, Call::ColorHdr,
        Call::ReleaseTargets, Call::ResizeSdr, Call::ColorSdr, Call::CreateTarget},
        "color failure performs the full rollback sequence");
}

void test_failed_rollback_resize_keeps_real_format_and_invalid_color() {
    State state;
    (void)plan_update(state, Mode::Hdr, 2, 0);
    FakeBackend backend{
        .resize_results = {Failure::None, Failure::Transient},
        .color_results = {Failure::Unsupported},
    };
    const auto result = execute_transaction(state, backend, 0);
    check(result.needs_redraw && result.failure == Failure::Transient,
        "failed rollback resize remains retryable and requires redraw");
    check(state.actual_format == SurfaceFormat::Rgba16Float &&
        state.applied_color_space == AppliedColorSpace::Invalid && state.target_valid,
        "failed rollback records the desired format as real but color space as invalid");
    const auto reversed = plan_update(state, Mode::Sdr, 2, 1);
    check(reversed.action == Action::ExecuteTransaction,
        "reversing to SDR does not mistake invalid FP16 output for applied SDR");
}

void test_unsupported_incomplete_rollback_remains_retryable() {
    State state;
    (void)plan_update(state, Mode::Hdr, 2, 0);
    FakeBackend backend{
        .resize_results = {Failure::None, Failure::Unsupported},
        .color_results = {Failure::Unsupported},
    };
    const auto result = execute_transaction(state, backend, 0);
    check(result.failure == Failure::Transient && !state.unsupported_blocked &&
        state.applied_color_space == AppliedColorSpace::Invalid,
        "an incomplete unsupported rollback stays retryable");
    check(plan_update(state, Mode::Hdr, 2, 249).action == Action::None &&
        plan_update(state, Mode::Hdr, 2, 250).action == Action::ExecuteTransaction,
        "invalid fallback retries after transient backoff");
}

void test_failed_hdr_to_sdr_rollback_never_reports_ready_sdr() {
    auto state = hdr_state(9);
    (void)plan_update(state, Mode::Sdr, 9, 0);
    FakeBackend backend{
        .resize_results = {Failure::None, Failure::Transient},
        .color_results = {Failure::Unsupported},
    };
    const auto result = execute_transaction(state, backend, 0);
    check(!result.applied && result.needs_redraw &&
        result.failure == Failure::Transient,
        "failed HDR rollback keeps the SDR request retryable");
    check(state.actual_format == SurfaceFormat::Bgra8 &&
        state.applied_color_space == AppliedColorSpace::Invalid && state.target_valid,
        "failed HDR rollback records real BGRA format without inventing SDR color space");
    check(plan_update(state, Mode::Sdr, 9, 249).action == Action::None &&
        plan_update(state, Mode::Sdr, 9, 250).action == Action::ExecuteTransaction,
        "invalid BGRA output is retried instead of being mistaken for ready SDR");
}

void test_failed_rollback_color_remains_invalid() {
    State state;
    (void)plan_update(state, Mode::Hdr, 3, 0);
    FakeBackend backend{
        .resize_results = {Failure::None, Failure::None},
        .color_results = {Failure::Unsupported, Failure::Transient},
    };
    const auto result = execute_transaction(state, backend, 0);
    check(result.failure == Failure::Transient && result.needs_redraw,
        "rollback color failure outranks the original unsupported result");
    check(state.actual_format == SurfaceFormat::Bgra8 &&
        state.applied_color_space == AppliedColorSpace::Invalid && state.target_valid,
        "failed rollback color space is never published as valid SDR");
}

void test_target_creation_failure_does_not_commit_ready_state() {
    State state;
    (void)plan_update(state, Mode::Hdr, 1, 0);
    FakeBackend backend{.target_results = {Failure::Transient}};
    auto result = execute_transaction(state, backend, 0);
    check(!result.applied && result.needs_redraw && !state.target_valid &&
        state.actual_format == SurfaceFormat::Rgba16Float &&
        state.applied_color_space == AppliedColorSpace::Hdr,
        "target failure preserves known format/color but never marks output ready");

    check(plan_update(state, Mode::Hdr, 1, 250).action == Action::ExecuteTransaction,
        "target creation failure is retried after backoff");
    FakeBackend recovery;
    result = execute_transaction(state, recovery, 250);
    check(result.applied && state.target_valid,
        "target-only retry recovers without another resize");
    check_calls(recovery, {Call::ColorHdr, Call::CreateTarget},
        "target-only recovery revalidates color and recreates the target");
}

void test_unsupported_unblocks_when_monitor_changes() {
    State state;
    (void)plan_update(state, Mode::Hdr, 10, 0);
    FakeBackend backend{
        .resize_results = {Failure::None, Failure::None},
        .color_results = {Failure::Unsupported, Failure::None},
    };
    (void)execute_transaction(state, backend, 0);
    const auto plan = plan_update(state, Mode::Hdr, 11, 1);
    check(plan.action == Action::ExecuteTransaction && plan.monitor_changed,
        "new monitor generation clears an unsupported capability block");
}

void test_transient_failures_use_exponential_backoff() {
    State state;
    (void)plan_update(state, Mode::Hdr, 0, 0);
    FakeBackend first{.resize_results = {Failure::Transient}};
    (void)execute_transaction(state, first, 0);
    check(state.retry_not_before_ms == 250,
        "first transient retry delay is 250ms");

    FakeBackend second{.resize_results = {Failure::Transient}};
    (void)execute_transaction(state, second, 250);
    check(state.retry_not_before_ms == 750 &&
        plan_update(state, Mode::Hdr, 0, 749).action == Action::None &&
        plan_update(state, Mode::Hdr, 0, 750).action == Action::ExecuteTransaction,
        "second transient retry doubles to 500ms");
}

void test_device_loss_requests_rebuild() {
    State state;
    (void)plan_update(state, Mode::Hdr, 5, 0);
    FakeBackend backend{.resize_results = {Failure::DeviceLost}};
    const auto result = execute_transaction(state, backend, 0);
    check(result.rebuild_device && result.failure == Failure::DeviceLost &&
        !state.target_valid && state.applied_color_space == AppliedColorSpace::Invalid,
        "device loss invalidates resources and requests device rebuild");
    check(plan_update(state, Mode::Hdr, 5, 1).action == Action::RebuildDevice,
        "device loss bypasses normal transaction retries");
    check_calls(backend, {Call::ReleaseTargets, Call::ResizeHdr},
        "device loss does not call into the dead device again");

    commit_device_rebuild(state, SurfaceFormat::Bgra8,
        AppliedColorSpace::Sdr, true);
    check(plan_update(state, Mode::Hdr, 5, 2).action == Action::ExecuteTransaction,
        "known SDR rebuild resumes the pending HDR request");
}

void test_device_loss_at_every_transaction_stage_requests_rebuild() {
    {
        auto state = hdr_state(1);
        (void)plan_update(state, Mode::Hdr, 2, 0);
        FakeBackend backend{.color_results = {Failure::DeviceLost}};
        const auto result = execute_transaction(state, backend, 0);
        check(result.rebuild_device && !state.target_valid,
            "device loss during color-space revalidation requests rebuild");
        check_calls(backend, {Call::ColorHdr},
            "color-space device loss does not make further backend calls");
    }
    {
        State state;
        (void)plan_update(state, Mode::Hdr, 1, 0);
        FakeBackend backend{
            .resize_results = {Failure::None, Failure::DeviceLost},
            .color_results = {Failure::Unsupported},
        };
        const auto result = execute_transaction(state, backend, 0);
        check(result.rebuild_device && result.needs_redraw,
            "device loss during rollback resize requests rebuild and preserves redraw signal");
        check_calls(backend, {Call::ReleaseTargets, Call::ResizeHdr, Call::ColorHdr,
            Call::ReleaseTargets, Call::ResizeSdr},
            "rollback resize device loss stops before color restore or target creation");
    }
    {
        State state;
        (void)plan_update(state, Mode::Hdr, 1, 0);
        FakeBackend backend{
            .resize_results = {Failure::None, Failure::None},
            .color_results = {Failure::Unsupported, Failure::DeviceLost},
        };
        const auto result = execute_transaction(state, backend, 0);
        check(result.rebuild_device && result.needs_redraw,
            "device loss during rollback color restore requests rebuild");
        check_calls(backend, {Call::ReleaseTargets, Call::ResizeHdr, Call::ColorHdr,
            Call::ReleaseTargets, Call::ResizeSdr, Call::ColorSdr},
            "rollback color device loss stops before target creation");
    }
    {
        State state;
        (void)plan_update(state, Mode::Hdr, 1, 0);
        FakeBackend backend{.target_results = {Failure::DeviceLost}};
        const auto result = execute_transaction(state, backend, 0);
        check(result.rebuild_device && result.needs_redraw && !state.target_valid,
            "device loss during target creation requests rebuild");
        check_calls(backend, {Call::ReleaseTargets, Call::ResizeHdr,
            Call::ColorHdr, Call::CreateTarget},
            "target creation device loss ends the transaction immediately");
    }
}

void test_preference_reversal_uses_known_good_output() {
    auto state = hdr_state(7);
    check(plan_update(state, Mode::Sdr, 7, 0).action == Action::ExecuteTransaction,
        "HDR to SDR preference change plans a switch");
    const auto reversed = plan_update(state, Mode::Hdr, 7, 1);
    check(reversed.action == Action::None && state.desired_is_applied(),
        "preference reversal accepts the still-valid HDR output immediately");

    (void)plan_update(state, Mode::Sdr, 7, 2);
    FakeBackend backend;
    const auto result = execute_transaction(state, backend, 2);
    check(result.applied && result.needs_redraw &&
        state.actual_format == SurfaceFormat::Bgra8 &&
        state.applied_color_space == AppliedColorSpace::Sdr,
        "successful HDR to SDR switch commits a valid SDR output");
}

void test_hdr_monitor_to_hdr_monitor_revalidates_without_resize() {
    auto state = hdr_state(20);
    const auto plan = plan_update(state, Mode::Hdr, 21, 0);
    check(plan.action == Action::ExecuteTransaction && plan.monitor_changed,
        "moving between HDR monitors requires color-space revalidation");
    FakeBackend backend;
    const auto result = execute_transaction(state, backend, 0);
    check(result.applied && !result.needs_redraw &&
        state.color_space_monitor_generation == 21,
        "same-format monitor move revalidates without discarding contents");
    check_calls(backend, {Call::ColorHdr},
        "same-format monitor move only checks and applies color space");
}

void test_same_format_transient_preserves_scrgb_until_retry() {
    auto state = hdr_state(30);
    (void)plan_update(state, Mode::Hdr, 31, 0);
    FakeBackend backend{.color_results = {Failure::Transient}};
    const auto result = execute_transaction(state, backend, 0);
    check(!result.applied && !result.needs_redraw &&
        result.failure == Failure::Transient,
        "same-format color failure is retryable without discarding contents");
    check(state.actual_format == SurfaceFormat::Rgba16Float &&
        state.applied_color_space == AppliedColorSpace::Hdr &&
        state.color_space_monitor_generation == 30 && state.target_valid,
        "same-format transient failure preserves the active scRGB state");
    check(plan_update(state, Mode::Hdr, 31, 249).action == Action::None &&
        plan_update(state, Mode::Hdr, 31, 250).action == Action::ExecuteTransaction,
        "same-format transient failure retries after backoff");
    check_calls(backend, {Call::ColorHdr},
        "same-format transient failure does not resize or recreate the target");
}

void test_same_format_unsupported_hdr_commits_explicit_sdr_fallback() {
    auto state = hdr_state(40);
    (void)plan_update(state, Mode::Hdr, 41, 0);
    FakeBackend backend{
        .resize_results = {Failure::None},
        .color_results = {Failure::Unsupported, Failure::None},
    };
    const auto result = execute_transaction(state, backend, 0);
    check(!result.applied && result.needs_redraw &&
        result.failure == Failure::Unsupported && state.unsupported_blocked,
        "unsupported same-format HDR request blocks only after SDR fallback");
    check(state.actual_format == SurfaceFormat::Bgra8 &&
        state.applied_color_space == AppliedColorSpace::Sdr &&
        state.color_space_monitor_generation == 41 && state.target_valid,
        "same-format unsupported HDR commits BGRA8 G22 SDR atomically");
    check(plan_update(state, Mode::Hdr, 41, 100'000).action == Action::None,
        "successful fallback remains blocked on its capability generation");
    const auto policy_plan = plan_update(state, Mode::Hdr, 41, 1, 100'001);
    check(policy_plan.action == Action::ExecuteTransaction &&
        policy_plan.policy_changed,
        "policy generation change unblocks an unsupported HDR request");
    check_calls(backend, {Call::ColorHdr, Call::ReleaseTargets,
        Call::ResizeSdr, Call::ColorSdr, Call::CreateTarget},
        "same-format HDR rejection executes the explicit SDR fallback transaction");
}

void test_failed_same_format_hdr_fallback_preserves_previous_scrgb() {
    auto state = hdr_state(50);
    (void)plan_update(state, Mode::Hdr, 51, 0);
    FakeBackend backend{
        .resize_results = {Failure::Transient},
        .color_results = {Failure::Unsupported},
    };
    const auto result = execute_transaction(state, backend, 0);
    check(result.failure == Failure::Transient && !state.unsupported_blocked,
        "incomplete same-format HDR fallback stays retryable");
    check(state.actual_format == SurfaceFormat::Rgba16Float &&
        state.applied_color_space == AppliedColorSpace::Hdr &&
        state.color_space_monitor_generation == 50 && state.target_valid,
        "failed fallback resize restores the still-active scRGB state");
    check_calls(backend, {Call::ColorHdr, Call::ReleaseTargets,
        Call::ResizeSdr, Call::CreateTarget},
        "failed fallback recreates only the released scRGB target");
}

void test_unsupported_hdr_resize_revalidates_sdr_before_blocking() {
    State state;
    (void)plan_update(state, Mode::Hdr, 60, 0);
    FakeBackend backend{.resize_results = {Failure::Unsupported}};
    const auto result = execute_transaction(state, backend, 0);
    check(result.failure == Failure::Unsupported && state.unsupported_blocked &&
        state.actual_format == SurfaceFormat::Bgra8 &&
        state.applied_color_space == AppliedColorSpace::Sdr && state.target_valid,
        "unsupported HDR resize blocks only after revalidating BGRA8 SDR");
    check_calls(backend, {Call::ReleaseTargets, Call::ResizeHdr,
        Call::ColorSdr, Call::CreateTarget},
        "unsupported HDR resize completes the explicit SDR fallback");
}

} // namespace

int main() {
    test_successful_sdr_to_hdr_switch();
    test_hdr_effective_requires_source_monitor_and_surface();
    test_resize_failure_preserves_previous_state_and_backs_off();
    test_color_failure_rolls_back_completely_and_blocks_unsupported();
    test_failed_rollback_resize_keeps_real_format_and_invalid_color();
    test_unsupported_incomplete_rollback_remains_retryable();
    test_failed_hdr_to_sdr_rollback_never_reports_ready_sdr();
    test_failed_rollback_color_remains_invalid();
    test_target_creation_failure_does_not_commit_ready_state();
    test_unsupported_unblocks_when_monitor_changes();
    test_transient_failures_use_exponential_backoff();
    test_device_loss_requests_rebuild();
    test_device_loss_at_every_transaction_stage_requests_rebuild();
    test_preference_reversal_uses_known_good_output();
    test_hdr_monitor_to_hdr_monitor_revalidates_without_resize();
    test_same_format_transient_preserves_scrgb_until_retry();
    test_same_format_unsupported_hdr_commits_explicit_sdr_fallback();
    test_failed_same_format_hdr_fallback_preserves_previous_scrgb();
    test_unsupported_hdr_resize_revalidates_sdr_before_blocking();

    if (failures != 0) {
        std::cerr << failures << " output mode state test(s) failed\n";
        return 1;
    }
    std::cout << "All output mode state tests passed\n";
    return 0;
}
