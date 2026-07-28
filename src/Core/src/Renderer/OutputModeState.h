#pragma once

#include <algorithm>
#include <cstdint>
#include <type_traits>

namespace iPhoneMirror::renderer::output {

enum class Mode : std::uint8_t {
    Sdr,
    Hdr,
};

enum class SurfaceFormat : std::uint8_t {
    Bgra8,
    Rgba16Float,
};

// Invalid means that the swap-chain color space is unknown and must not be
// treated as applied merely because its buffer format matches the request.
enum class AppliedColorSpace : std::uint8_t {
    Invalid,
    Sdr,
    Hdr,
};

enum class Failure : std::uint8_t {
    None,
    Unsupported,
    Transient,
    DeviceLost,
};

enum class Action : std::uint8_t {
    None,
    ExecuteTransaction,
    RebuildDevice,
};

struct State {
    SurfaceFormat actual_format{SurfaceFormat::Bgra8};
    AppliedColorSpace applied_color_space{AppliedColorSpace::Sdr};
    bool target_valid{true};
    Mode desired_mode{Mode::Sdr};

    // A color-space setting is valid only for the monitor generation on which
    // it was checked and applied. Moving between two HDR monitors therefore
    // revalidates the existing scRGB swap chain without forcing a resize.
    std::uint64_t monitor_generation{};
    std::uint64_t color_space_monitor_generation{};
    // Policy changes can make a previously unsupported request worth trying
    // again even when they resolve to the same output mode.
    std::uint64_t policy_generation{};

    Failure last_failure{Failure::None};
    std::uint32_t transient_failures{};
    std::uint64_t retry_not_before_ms{};
    bool unsupported_blocked{};

    [[nodiscard]] bool desired_is_applied() const noexcept;
};

struct Plan {
    Action action{Action::None};
    bool desired_changed{};
    bool monitor_changed{};
    bool policy_changed{};
};

struct TransactionResult {
    bool attempted{};
    bool applied{};
    // ResizeBuffers discards swap-chain contents even when a later operation
    // fails and the transaction rolls back. Callers must repaint retained
    // frames whenever this flag is set.
    bool needs_redraw{};
    bool rebuild_device{};
    Failure failure{Failure::None};
};

[[nodiscard]] constexpr SurfaceFormat format_for(Mode mode) noexcept {
    return mode == Mode::Hdr ? SurfaceFormat::Rgba16Float : SurfaceFormat::Bgra8;
}

[[nodiscard]] constexpr AppliedColorSpace color_space_for(Mode mode) noexcept {
    return mode == Mode::Hdr ? AppliedColorSpace::Hdr : AppliedColorSpace::Sdr;
}

[[nodiscard]] constexpr Mode mode_for(AppliedColorSpace color_space) noexcept {
    return color_space == AppliedColorSpace::Hdr ? Mode::Hdr : Mode::Sdr;
}

[[nodiscard]] constexpr bool hdr_output_is_effective(bool source_hdr,
    bool monitor_hdr, bool actual_hdr_surface) noexcept {
    return source_hdr && monitor_hdr && actual_hdr_surface;
}

inline bool State::desired_is_applied() const noexcept {
    return target_valid && actual_format == format_for(desired_mode) &&
        applied_color_space == color_space_for(desired_mode) &&
        color_space_monitor_generation == monitor_generation;
}

namespace detail {

[[nodiscard]] constexpr int failure_rank(Failure failure) noexcept {
    switch (failure) {
    case Failure::None: return 0;
    case Failure::Unsupported: return 1;
    case Failure::Transient: return 2;
    case Failure::DeviceLost: return 3;
    }
    return 0;
}

[[nodiscard]] constexpr Failure merge_failure(Failure left, Failure right) noexcept {
    return failure_rank(right) > failure_rank(left) ? right : left;
}

[[nodiscard]] constexpr std::uint64_t transient_retry_delay_ms(
    std::uint32_t failure_count) noexcept {
    if (failure_count == 0) return 0;
    constexpr std::uint64_t InitialDelayMs = 250;
    constexpr std::uint64_t MaximumDelayMs = 8'000;
    const auto shift = std::min<std::uint32_t>(failure_count - 1, 5);
    return std::min(InitialDelayMs << shift, MaximumDelayMs);
}

inline void clear_failure(State& state) noexcept {
    state.last_failure = Failure::None;
    state.transient_failures = 0;
    state.retry_not_before_ms = 0;
    state.unsupported_blocked = false;
}

inline void record_failure(State& state, Failure failure, std::uint64_t now_ms) noexcept {
    state.last_failure = failure;
    state.unsupported_blocked = failure == Failure::Unsupported;
    if (failure == Failure::Transient) {
        ++state.transient_failures;
        state.retry_not_before_ms = now_ms +
            transient_retry_delay_ms(state.transient_failures);
    } else {
        state.transient_failures = 0;
        state.retry_not_before_ms = 0;
    }
}

inline void invalidate_device(State& state) noexcept {
    state.applied_color_space = AppliedColorSpace::Invalid;
    state.color_space_monitor_generation = 0;
    state.target_valid = false;
}

} // namespace detail

// Updates policy inputs and decides whether the caller should execute a
// transaction. Unsupported is sticky only for the same desired mode, monitor,
// and policy generation. Transient failures use deterministic exponential
// backoff, while device loss always bypasses normal retries.
[[nodiscard]] inline Plan plan_update(State& state, Mode desired_mode,
    std::uint64_t monitor_generation, std::uint64_t policy_generation,
    std::uint64_t now_ms) noexcept {
    Plan plan;
    plan.desired_changed = desired_mode != state.desired_mode;
    plan.monitor_changed = monitor_generation != state.monitor_generation;
    plan.policy_changed = policy_generation != state.policy_generation;
    state.desired_mode = desired_mode;
    state.monitor_generation = monitor_generation;
    state.policy_generation = policy_generation;

    if (plan.desired_changed || plan.monitor_changed || plan.policy_changed) {
        state.unsupported_blocked = false;
        state.transient_failures = 0;
        state.retry_not_before_ms = 0;
        if (state.last_failure != Failure::DeviceLost)
            state.last_failure = Failure::None;
    }

    if (state.last_failure == Failure::DeviceLost) {
        plan.action = Action::RebuildDevice;
        return plan;
    }
    if (state.desired_is_applied()) {
        detail::clear_failure(state);
        return plan;
    }
    if (state.unsupported_blocked || now_ms < state.retry_not_before_ms)
        return plan;
    plan.action = Action::ExecuteTransaction;
    return plan;
}

// Existing callers that do not expose a policy generation keep the currently
// published generation rather than accidentally resetting it.
[[nodiscard]] inline Plan plan_update(State& state, Mode desired_mode,
    std::uint64_t monitor_generation, std::uint64_t now_ms) noexcept {
    return plan_update(state, desired_mode, monitor_generation,
        state.policy_generation, now_ms);
}

// Publishes a known state after the render thread has rebuilt its D3D device
// and swap chain. A valid color space is tied to the current monitor generation.
inline void commit_device_rebuild(State& state, SurfaceFormat actual_format,
    AppliedColorSpace applied_color_space, bool target_valid) noexcept {
    state.actual_format = actual_format;
    state.applied_color_space = applied_color_space;
    state.color_space_monitor_generation =
        applied_color_space == AppliedColorSpace::Invalid
        ? 0 : state.monitor_generation;
    state.target_valid = target_valid;
    detail::clear_failure(state);
}

// Backend contract:
//   void release_targets() noexcept;
//   Failure resize(SurfaceFormat) noexcept;
//   Failure set_color_space(Mode) noexcept;
//   Failure create_target() noexcept;
// A failed resize must leave the previous buffer format active, matching the
// ResizeBuffers contract. The executor records every successful resize as a
// redraw requirement and never commits a valid output before target creation.
template <typename Backend>
[[nodiscard]] TransactionResult execute_transaction(State& state,
    Backend& backend, std::uint64_t now_ms) noexcept {
    static_assert(std::is_same_v<decltype(backend.resize(SurfaceFormat::Bgra8)), Failure>);
    static_assert(std::is_same_v<decltype(backend.set_color_space(Mode::Sdr)), Failure>);
    static_assert(std::is_same_v<decltype(backend.create_target()), Failure>);

    TransactionResult result{.attempted = true};
    const auto previous_format = state.actual_format;
    const auto previous_color_space = state.applied_color_space;
    const auto previous_color_generation = state.color_space_monitor_generation;
    const auto desired_format = format_for(state.desired_mode);
    Failure failure = Failure::None;
    bool resized_to_desired{};
    bool validated_fallback{};

    const auto add_failure = [&](Failure value) noexcept {
        failure = detail::merge_failure(failure, value);
    };
    const auto finish_failure = [&]() noexcept {
        if (failure == Failure::None) failure = Failure::Transient;
        // Unsupported is sticky only after a complete fallback transaction.
        // Otherwise retry it as a transient failure because the current
        // format/color/RTV tuple has not been validated as usable.
        if (failure == Failure::Unsupported &&
            (!validated_fallback || !state.target_valid ||
             state.applied_color_space == AppliedColorSpace::Invalid ||
             state.color_space_monitor_generation != state.monitor_generation)) {
            failure = Failure::Transient;
        }
        if (failure == Failure::DeviceLost) detail::invalidate_device(state);
        detail::record_failure(state, failure, now_ms);
        result.applied = false;
        result.rebuild_device = failure == Failure::DeviceLost;
        result.failure = failure;
        return result;
    };
    const auto create_target = [&]() noexcept {
        const auto target_result = backend.create_target();
        if (target_result == Failure::None) {
            state.target_valid = true;
            return true;
        }
        state.target_valid = false;
        add_failure(target_result);
        if (target_result == Failure::DeviceLost) detail::invalidate_device(state);
        return false;
    };

    // HDR capability failures must leave a known BGRA8/G22 SDR output behind.
    // This is deliberately independent of the pre-transaction state: a
    // same-format HDR revalidation can fail after moving to another monitor.
    const auto fallback_hdr_to_sdr = [&]() noexcept {
        if (state.actual_format != SurfaceFormat::Bgra8) {
            backend.release_targets();
            state.target_valid = false;
            const auto fallback_resize = backend.resize(SurfaceFormat::Bgra8);
            if (fallback_resize != Failure::None) {
                add_failure(fallback_resize);
                if (fallback_resize == Failure::DeviceLost) {
                    detail::invalidate_device(state);
                    return false;
                }

                // ResizeBuffers failed, so the format and color space that
                // preceded the fallback attempt are still active.
                state.actual_format = desired_format;
                state.applied_color_space = resized_to_desired
                    ? AppliedColorSpace::Invalid : previous_color_space;
                state.color_space_monitor_generation = resized_to_desired
                    ? 0 : previous_color_generation;
                (void)create_target();
                return false;
            }
            result.needs_redraw = true;
            state.actual_format = SurfaceFormat::Bgra8;
            state.applied_color_space = AppliedColorSpace::Invalid;
            state.color_space_monitor_generation = 0;
        }

        const auto fallback_color = backend.set_color_space(Mode::Sdr);
        if (fallback_color != Failure::None) {
            add_failure(fallback_color);
            state.applied_color_space = AppliedColorSpace::Invalid;
            state.color_space_monitor_generation = 0;
            if (fallback_color == Failure::DeviceLost) {
                detail::invalidate_device(state);
                return false;
            }
            if (!state.target_valid) (void)create_target();
            return false;
        }

        state.applied_color_space = AppliedColorSpace::Sdr;
        state.color_space_monitor_generation = state.monitor_generation;
        if (!state.target_valid && !create_target()) return false;
        validated_fallback = true;
        return true;
    };

    if (state.actual_format != desired_format) {
        backend.release_targets();
        state.target_valid = false;
        const auto resize_result = backend.resize(desired_format);
        if (resize_result != Failure::None) {
            add_failure(resize_result);
            if (resize_result == Failure::DeviceLost) {
                detail::invalidate_device(state);
                return finish_failure();
            }

            // Resize failed, so the previous format and color-space setting
            // remain active. Recreate only the released target view.
            state.actual_format = previous_format;
            state.applied_color_space = previous_color_space;
            state.color_space_monitor_generation = previous_color_generation;
            if (resize_result == Failure::Unsupported &&
                state.desired_mode == Mode::Hdr) {
                (void)fallback_hdr_to_sdr();
            } else {
                (void)create_target();
                validated_fallback = state.target_valid &&
                    state.applied_color_space != AppliedColorSpace::Invalid;
            }
            return finish_failure();
        }

        resized_to_desired = true;
        result.needs_redraw = true;
        state.actual_format = desired_format;
        state.applied_color_space = AppliedColorSpace::Invalid;
        state.color_space_monitor_generation = 0;
    }

    const auto color_result = backend.set_color_space(state.desired_mode);
    if (color_result != Failure::None) {
        add_failure(color_result);
        if (color_result == Failure::DeviceLost) {
            detail::invalidate_device(state);
            return finish_failure();
        }

        if (!resized_to_desired) {
            // SetColorSpace1 does not replace the active color space when it
            // fails. Preserve a still-valid scRGB output and its generation;
            // monitor revalidation will retry after transient backoff.
            state.applied_color_space = previous_color_space;
            state.color_space_monitor_generation = previous_color_generation;
            if (state.desired_mode == Mode::Hdr &&
                color_result == Failure::Unsupported) {
                (void)fallback_hdr_to_sdr();
            } else {
                if (!state.target_valid) (void)create_target();
                validated_fallback = color_result == Failure::Unsupported &&
                    state.target_valid &&
                    state.applied_color_space != AppliedColorSpace::Invalid;
            }
            return finish_failure();
        }

        state.applied_color_space = AppliedColorSpace::Invalid;
        state.color_space_monitor_generation = 0;

        if (state.desired_mode == Mode::Hdr &&
            color_result == Failure::Unsupported) {
            (void)fallback_hdr_to_sdr();
            return finish_failure();
        }

        backend.release_targets();
        state.target_valid = false;
        const auto rollback_resize = backend.resize(previous_format);
        if (rollback_resize == Failure::None) {
            result.needs_redraw = true;
            state.actual_format = previous_format;
            state.applied_color_space = AppliedColorSpace::Invalid;
            state.color_space_monitor_generation = 0;
            if (previous_color_space != AppliedColorSpace::Invalid) {
                const auto rollback_color = backend.set_color_space(
                    mode_for(previous_color_space));
                if (rollback_color == Failure::None) {
                    state.applied_color_space = previous_color_space;
                    state.color_space_monitor_generation = state.monitor_generation;
                    validated_fallback = true;
                } else {
                    add_failure(rollback_color);
                    if (rollback_color == Failure::DeviceLost) {
                        detail::invalidate_device(state);
                        return finish_failure();
                    }
                }
            }
        } else {
            // The failed rollback leaves the successfully resized desired
            // format active, but its color space is still invalid.
            add_failure(rollback_resize);
            state.actual_format = desired_format;
            state.applied_color_space = AppliedColorSpace::Invalid;
            state.color_space_monitor_generation = 0;
            if (rollback_resize == Failure::DeviceLost) {
                detail::invalidate_device(state);
                return finish_failure();
            }
        }

        if (!state.target_valid) (void)create_target();
        return finish_failure();
    }

    state.applied_color_space = color_space_for(state.desired_mode);
    state.color_space_monitor_generation = state.monitor_generation;
    if (!state.target_valid && !create_target()) return finish_failure();

    if (!state.desired_is_applied()) {
        add_failure(Failure::Transient);
        return finish_failure();
    }
    detail::clear_failure(state);
    result.applied = true;
    return result;
}

} // namespace iPhoneMirror::renderer::output
