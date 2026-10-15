import CoreAudio
import Foundation

// Whole-process muting through Core Audio process taps (macOS 14.2+). A private tap with
// muteBehavior .muted silences the process's output for as long as the tap exists; destroying
// the tap restores the sound. No audio is read or recorded.

private var taps: [pid_t: AudioObjectID] = [:]
private let tapLock = NSLock()

private func processObject(for pid: pid_t) -> AudioObjectID? {
    var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyTranslatePIDToProcessObject,
                                             mScope: kAudioObjectPropertyScopeGlobal,
                                             mElement: kAudioObjectPropertyElementMain)
    var qualifier = pid
    var object = AudioObjectID(kAudioObjectUnknown)
    var size = UInt32(MemoryLayout<AudioObjectID>.size)
    let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address,
                                            UInt32(MemoryLayout<pid_t>.size), &qualifier, &size, &object)
    return status == noErr && object != kAudioObjectUnknown ? object : nil
}

/// Returns 1 when the requested state is in effect, 0 when the process has no audio object yet
/// (retry later), and a negative OSStatus-derived value on failure.
@_cdecl("eom_audio_set_muted")
public func eom_audio_set_muted(_ pid: Int32, _ muted: Int32) -> Int32 {
    tapLock.lock(); defer { tapLock.unlock() }
    if muted == 0 {
        guard let tap = taps.removeValue(forKey: pid) else { return 1 }
        let status = AudioHardwareDestroyProcessTap(tap)
        if status != noErr { log(.warning, "Could not remove audio mute for pid \(pid): \(status)") }
        return 1
    }
    if taps[pid] != nil { return 1 }
    guard let object = processObject(for: pid) else {
        log(.verbose, "No Core Audio process object for pid \(pid) yet")
        return 0
    }
    let description = CATapDescription(stereoMixdownOfProcesses: [object])
    description.name = "EVE-O Preview mute \(pid)"
    description.isPrivate = true
    description.muteBehavior = .muted
    var tap = AudioObjectID(kAudioObjectUnknown)
    let status = AudioHardwareCreateProcessTap(description, &tap)
    guard status == noErr, tap != kAudioObjectUnknown else {
        log(.warning, "Could not mute pid \(pid): \(status)")
        return status == noErr ? -1 : -abs(Int32(truncatingIfNeeded: status))
    }
    taps[pid] = tap
    return 1
}

@_cdecl("eom_audio_is_muted")
public func eom_audio_is_muted(_ pid: Int32) -> Int32 {
    tapLock.lock(); defer { tapLock.unlock() }
    return taps[pid] != nil ? 1 : 0
}

@_cdecl("eom_audio_unmute_all")
public func eom_audio_unmute_all() {
    tapLock.lock(); defer { tapLock.unlock() }
    for (_, tap) in taps { AudioHardwareDestroyProcessTap(tap) }
    taps.removeAll()
}

// MARK: process scheduling policy

/// Darwin background policy: efficiency cores, lowest priority and throttled disk I/O. Sockets the
/// process already has open are unaffected; sockets opened while in background are throttled.
@_cdecl("eom_set_background_policy")
public func eom_set_background_policy(_ pid: Int32, _ background: Int32) -> Int32 {
    let result = setpriority(PRIO_DARWIN_PROCESS, id_t(pid), background != 0 ? PRIO_DARWIN_BG : 0)
    if result != 0 && errno != ESRCH { log(.warning, "Background policy for pid \(pid) failed: errno \(errno)") }
    return result == 0 ? 1 : 0
}
