#pragma once

#include "Capture/ICaptureSession.h"
#include "Media/MediaFoundationDecoder.h"

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <utility>

namespace iPhoneMirror::capture::detail {

struct DecoderPreferenceState {
    media::DecoderPreference preference{media::DecoderPreference::Auto};
    std::uint64_t generation{1};

    bool operator==(const DecoderPreferenceState&) const noexcept = default;
};

struct DecoderPreferenceUpdate {
    DecoderPreferenceState previous;
    DecoderPreferenceState current;
    bool changed{};
};

[[nodiscard]] constexpr std::uint64_t pack_decoder_preference_state(
    media::DecoderPreference preference, std::uint64_t generation) noexcept {
    return (generation << 8U) | static_cast<std::uint8_t>(preference);
}

[[nodiscard]] constexpr DecoderPreferenceState unpack_decoder_preference_state(
    std::uint64_t state) noexcept {
    return {
        static_cast<media::DecoderPreference>(state & 0xffU),
        state >> 8U,
    };
}

class DecoderSwitchCoordinator {
public:
    explicit DecoderSwitchCoordinator(
        media::DecoderPreference initial = media::DecoderPreference::Auto) noexcept
        : requested_(pack_decoder_preference_state(initial, 1)),
          applied_{initial, 1} {}

    [[nodiscard]] DecoderPreferenceState requested() const noexcept {
        return unpack_decoder_preference_state(
            requested_.load(std::memory_order_acquire));
    }

    [[nodiscard]] DecoderPreferenceUpdate request(
        media::DecoderPreference preference) {
        std::scoped_lock lock(commit_mutex_);
        const auto previous = requested();
        if (previous.preference == preference) {
            return {previous, previous, false};
        }
        const DecoderPreferenceState current{
            preference,
            previous.generation + 1U,
        };
        requested_.store(pack_decoder_preference_state(
            current.preference, current.generation), std::memory_order_release);
        return {previous, current, true};
    }

    [[nodiscard]] DecoderSwitchStatus status() const noexcept {
        std::scoped_lock lock(commit_mutex_);
        const auto requested = unpack_decoder_preference_state(
            requested_.load(std::memory_order_relaxed));
        const auto phase = requested == applied_
            ? DecoderSwitchPhase::Applied
            : failed_generation_ == requested.generation
                ? DecoderSwitchPhase::Failed
                : DecoderSwitchPhase::Pending;
        return {
            requested.preference,
            applied_.preference,
            requested.generation,
            applied_.generation,
            phase,
            runtime_mode_,
        };
    }

    template <typename Commit>
    [[nodiscard]] bool commit_if_current(
        DecoderPreferenceState expected, Commit&& commit,
        DecoderRuntimeMode runtime_mode = DecoderRuntimeMode::Unknown) {
        std::scoped_lock lock(commit_mutex_);
        if (requested() != expected) return false;
        std::invoke(std::forward<Commit>(commit));
        applied_ = expected;
        runtime_mode_ = runtime_mode;
        failed_generation_ = 0;
        return true;
    }

    void set_applied_runtime_mode(DecoderPreferenceState expected,
        DecoderRuntimeMode runtime_mode) noexcept {
        std::scoped_lock lock(commit_mutex_);
        if (applied_ == expected) runtime_mode_ = runtime_mode;
    }

    [[nodiscard]] bool mark_failed_if_current(
        DecoderPreferenceState expected) noexcept {
        std::scoped_lock lock(commit_mutex_);
        if (unpack_decoder_preference_state(
                requested_.load(std::memory_order_relaxed)) != expected ||
            applied_ == expected) {
            return false;
        }
        failed_generation_ = expected.generation;
        return true;
    }

private:
    // request() and the worker's final recheck/swap use this same boundary, so
    // a newer generation cannot be published between validation and commit.
    mutable std::mutex commit_mutex_;
    std::atomic_uint64_t requested_;
    DecoderPreferenceState applied_;
    std::uint64_t failed_generation_{};
    DecoderRuntimeMode runtime_mode_{DecoderRuntimeMode::Unknown};
};

// Candidate construction happens at the call site. This helper deliberately
// completes the candidate's first real decode call before taking the commit
// lock. A successful call with no output still means the MFT accepted the IDR.
template <typename Candidate, typename TrialDecode, typename Commit>
[[nodiscard]] bool trial_and_commit_decoder(
    DecoderSwitchCoordinator& coordinator,
    DecoderPreferenceState expected,
    Candidate& candidate,
    TrialDecode&& trial_decode,
    Commit&& commit,
    DecoderRuntimeMode runtime_mode = DecoderRuntimeMode::Unknown) {
    auto trial_output = std::invoke(
        std::forward<TrialDecode>(trial_decode), candidate);
    return coordinator.commit_if_current(expected, [&] {
        std::invoke(std::forward<Commit>(commit),
            std::move(candidate), std::move(trial_output));
    }, runtime_mode);
}

} // namespace iPhoneMirror::capture::detail
