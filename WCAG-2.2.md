# WCAG 2.2 Accessibility Assessment

## Document Status

| Field | Value |
| --- | --- |
| Component | `WinUIEx.Maps.MapControl` |
| Standard | WCAG 2.2 |
| Included levels | Level A: Yes; Level AA: Yes; Level AAA: No |
| Assessment type | Living engineering assessment |
| Last reviewed | 2026-08-29 |
| Formal conformance claim | No |

This document records the current accessibility evidence, known gaps, and
planned remediation for the reusable WinUI control. Level AAA criteria are
cataloged for completeness. They remain **Not Evaluated** unless targeted work
provides sufficient criterion-specific evidence, but Level AAA as a complete
conformance target remains outside the current assessment scope. This is not an
Accessibility Conformance Report (ACR), VPAT, certification, or legal claim of
conformance. The host application remains responsible for its surrounding UI,
content, configuration, custom layers, and end-to-end user experience.

## Status Definitions

| Status | Meaning |
| --- | --- |
| **Supports** | Available evidence currently demonstrates the criterion for the control's applicable built-in behavior. Manual product-level validation may still be required. |
| **Partially Supports** | Some applicable built-in behavior is covered, but a material path, content type, input mode, or accessibility surface is missing or unverified. |
| **Does Not Support** | An applicable requirement is known not to be met. |
| **Not Applicable** | The criterion does not apply to this reusable control's built-in behavior. It may still apply to the host application or application-authored content. |
| **Not Evaluated** | The criterion may apply, but sufficient targeted evidence has not yet been collected, or the criterion is outside the included conformance levels. |

Statuses are intentionally conservative. The existence of an API, style name,
or implementation plan is not sufficient evidence for **Supports**.

## Current Summary

| Status | Level A | Level AA | Level AAA | Total |
| --- | ---: | ---: | ---: | ---: |
| Supports | 10 | 2 | 1 | 11 |
| Partially Supports | 7 | 5 | 1 | 14 |
| Does Not Support | 0 | 0 | 0 | 0 |
| Not Applicable | 11 | 8 | 0 | 19 |
| Not Evaluated | 4 | 9 | 29 | 43 |

## Scope and Evidence Rules

The assessment includes:

- Built-in behavior of `WinUIEx.Maps.MapControl`.
- Public control APIs, default XAML template, built-in Azure-backed behavior,
  keyboard and pointer input, renderer-owned content, attribution, and
  authentication messages.
- Automated unit and live WinUI tests where cited by the evidence text.

The assessment excludes:

- Host-application layout, navigation, names, descriptions, and surrounding UI.
- Application-authored custom layers, tile imagery, map elements, icons, and
  event-handler behavior except where the control supplies a documented
  accessibility contract.
- Formal testing with Narrator, NVDA, Accessibility Insights, high-contrast
  themes, display scaling, all Windows text-scale settings, and all input
  devices unless explicitly cited below.

## Level A

| Criterion | Status | Current evidence and scope | Gap or planned action |
| --- | --- | --- | --- |
| **[1.1.1 Non-text Content (Level A)](http://www.w3.org/TR/WCAG20/#text-equiv-all)** | **Partially Supports** | Visible Azure vector labels produce a bounded text description of the displayed viewport. The description is exposed by the map automation peer and through a separate polite live region after map movement settles. The accessible-only vector style provides the same semantic output without drawing labels. | Raster imagery, polygons, polylines, icons, and application-authored layers still require map-element alternatives or author-supplied descriptions. Validate the generated descriptions with screen readers and representative Azure data. |
| **[1.2.1 Audio-only and Video-only (Prerecorded) (Level A)](http://www.w3.org/TR/WCAG20/#media-equiv-av-only-alt)** | **Not Applicable** | The control has no built-in prerecorded audio or video. | Host application responsibility. |
| **[1.2.2 Captions (Prerecorded) (Level A)](http://www.w3.org/TR/WCAG20/#media-equiv-captions)** | **Not Applicable** | The control has no built-in prerecorded synchronized media. | Host application responsibility. |
| **[1.2.3 Audio Description or Media Alternative (Prerecorded) (Level A)](http://www.w3.org/TR/WCAG20/#media-equiv-audio-desc)** | **Not Applicable** | The control has no built-in prerecorded synchronized media. | Host application responsibility. |
| **[1.3.1 Info and Relationships (Level A)](http://www.w3.org/TR/WCAG20/#content-structure-separation-programmatic)** | **Partially Supports** | The map root is a focusable WinUI `Control`; attribution is exposed as content with a polite live setting, and attribution links retain hyperlink semantics. | Geographic features, layer relationships, camera state, and lightweight `MapElement` instances have no UIA structure. Add semantic viewport data, descriptions, and virtual automation children, then validate them with assistive technology. |
| **[1.3.2 Meaningful Sequence (Level A)](http://www.w3.org/TR/WCAG20/#content-structure-separation-sequence)** | **Not Evaluated** | Visual layer and element ordering are deterministic, and attribution inline order is preserved. | No screen-reader traversal exists for geographic content, so meaningful automation order cannot yet be assessed. Define `MapTabIndex`, stable virtual-peer ordering, and focused traversal tests. |
| **[1.3.3 Sensory Characteristics (Level A)](http://www.w3.org/TR/WCAG20/#content-structure-separation-understanding)** | **Partially Supports** | Pan, zoom, rotate, and pitch have keyboard alternatives, and the viewport has a nonvisual geographic description with simplified and detailed modes. | Application-authored map-element selection does not yet have a built-in nonvisual focus and invocation model. |
| **[1.4.1 Use of Color (Level A)](http://www.w3.org/TR/WCAG20/#visual-audio-contrast-without-color)** | **Partially Supports** | Multiple Azure styles, including high-contrast light and dark, are available. | There is no targeted audit proving that built-in information is never conveyed by color alone across styles and element types. Add automatic high-contrast behavior and perform a targeted visual audit; application-authored layers remain application responsibility. |
| **[1.4.2 Audio Control (Level A)](http://www.w3.org/TR/WCAG20/#visual-audio-contrast-dis-audio)** | **Not Applicable** | The control does not play audio. | None for built-in behavior. |
| **[2.1.1 Keyboard (Level A)](http://www.w3.org/TR/WCAG20/#keyboard-operation-keyboard-operable)** | **Supports** | All built-in map navigation and screen-reader description functionality is keyboard operable. The shortcuts match Azure Maps: arrows pan 100 pixels; plus/equal and minus/hyphen/underscore zoom one level; Shift+Left/Right rotates 15 degrees; Shift+Up/Down changes pitch 10 degrees; Escape restores focus to the map; and Ctrl+Alt+D or Ctrl+Shift+D toggles description detail. Holding pan, zoom, rotate, or pitch keys produces continuous movement, while a quick press uses the documented discrete amount. Tab remains available for normal focus traversal. | Applications that attach additional behavior to their own map elements or surrounding controls remain responsible for providing equivalent keyboard operation for that application-authored functionality. |
| **[2.1.2 No Keyboard Trap (Level A)](http://www.w3.org/TR/WCAG20/#keyboard-operation-trapping)** | **Supports** | The map is a single tab stop and does not currently create an internal keyboard-focus cycle. | Re-evaluate when virtual map-element focus and traversal are added. |
| **[2.1.4 Character Key Shortcuts (Level A, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#character-key-shortcuts)** | **Supports** | Plus and minus shortcuts operate only while the map itself has focus. The control does not install application-wide single-character shortcuts. | Re-evaluate future description shortcuts and keep them modifier-qualified. |
| **[2.2.1 Timing Adjustable (Level A)](http://www.w3.org/TR/WCAG20/#time-limits-required-behaviors)** | **Not Applicable** | Built-in tasks do not impose a user-response time limit. | Host application responsibility. |
| **[2.2.2 Pause, Stop, Hide (Level A)](http://www.w3.org/TR/WCAG20/#time-limits-pause)** | **Not Applicable** | The control has no built-in automatically updating, blinking, or scrolling information that starts independently and persists for more than five seconds. Camera movement is user- or application-initiated and finite, and the Windows animation preference suppresses nonessential camera interpolation, inertia, focus transitions, and tile fades. | Re-evaluate if continuously updating overlays become built-in features. |
| **[2.3.1 Three Flashes or Below Threshold (Level A)](http://www.w3.org/TR/WCAG20/#seizure-does-not-violate)** | **Supports** | Built-in rendering and camera animations do not intentionally flash content. When Windows animations are disabled, camera interpolation, touch inertia, focus transitions, and raster/vector tile fades are suppressed. | Custom tile imagery and application-authored layers remain application responsibility; add targeted visual review to the accessibility test matrix. |
| **[2.4.1 Bypass Blocks (Level A)](http://www.w3.org/TR/WCAG20/#navigation-mechanisms-skip)** | **Not Applicable** | Bypass navigation is a page/application concern; the control is one focusable component. | Host application responsibility. |
| **[2.4.2 Page Titled (Level A)](http://www.w3.org/TR/WCAG20/#navigation-mechanisms-title)** | **Not Applicable** | The control does not own a page or window title. | Host application responsibility. |
| **[2.4.3 Focus Order (Level A)](http://www.w3.org/TR/WCAG20/#navigation-mechanisms-focus-order)** | **Partially Supports** | The root participates in normal XAML tab order and shows a focus state. | Lightweight map elements expose neither focus nor `MapTabIndex`. Implement and test deterministic logical traversal. |
| **[2.4.4 Link Purpose (In Context) (Level A)](http://www.w3.org/TR/WCAG20/#navigation-mechanisms-refs)** | **Partially Supports** | Visible attribution link text is also assigned as its automation name. | Link purpose depends on provider-supplied attribution text and has not been manually assessed. Application-provided interactive map content has no accessible context. |
| **[2.5.1 Pointer Gestures (Level A, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#pointer-gestures)** | **Supports** | Multipoint pinch/stretch, touch pan, rotation, and pitch have single-pointer or keyboard alternatives, including wheel, double-tap, plus/minus, arrows, and Shift+arrow shortcuts. | Application-authored gesture behavior remains application responsibility. |
| **[2.5.2 Pointer Cancellation (Level A, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#pointer-cancellation)** | **Not Evaluated** | Tap, right-tap, pointer, and manipulation handlers use WinUI routed input, but no focused test assesses down-event activation, cancellation, or undo semantics for all map interactions. | Add pointer cancellation and drag-threshold tests, including future map-element invocation. |
| **[2.5.3 Label in Name (Level A, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#label-in-name)** | **Supports** | Attribution hyperlinks use their visible text as the UIA name. The root has no built-in visible text label. | Require accessible names for interactive map elements and test that any visible title is contained in the accessible name. |
| **[2.5.4 Motion Actuation (Level A, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#motion-actuation)** | **Not Applicable** | The control does not use device motion or user gesture motion detected by sensors. Touch manipulation is pointer input covered by 2.5.1. | Host application responsibility for sensor-driven camera behavior. |
| **[3.1.1 Language of Page (Level A)](http://www.w3.org/TR/WCAG20/#meaning-doc-lang-id)** | **Not Applicable** | The control does not own the host page's default language. | Host applications must set the appropriate language. Generated map descriptions must use the control's effective language and culture. |
| **[3.2.1 On Focus (Level A)](http://www.w3.org/TR/WCAG20/#consistent-behavior-receive-focus)** | **Supports** | Receiving focus changes only the focus visual and does not initiate a context change. | Re-evaluate when virtual map-element focus is added. |
| **[3.2.2 On Input (Level A)](http://www.w3.org/TR/WCAG20/#consistent-behavior-unpredictable-change)** | **Supports** | Keyboard, pointer, and touch input perform the requested map operation without unrelated navigation or a change of application context. Runtime changes to the Windows animation preference alter only motion presentation and do not recreate the control or change its context. | Re-evaluate when accessible map-element invocation is added. |
| **[3.2.6 Consistent Help (Level A, WCAG 2.2 only)](https://www.w3.org/TR/WCAG22/#consistent-help)** | **Not Evaluated** | The control provides resource-backed keyboard help text but does not own repeated page-level help mechanisms. | Evaluate applicability within a reusable control and document host-application responsibilities. |
| **[3.3.1 Error Identification (Level A)](http://www.w3.org/TR/WCAG20/#minimize-error-identified)** | **Supports** | Missing or invalid Azure authentication is surfaced through a visible WinUI `InfoBar` with a complete accessible name and assertive live-region semantics. | End-to-end screen-reader behavior has not been evaluated, and other acquisition failures are diagnostics-only by design. |
| **[3.3.2 Labels or Instructions (Level A)](http://www.w3.org/TR/WCAG20/#minimize-error-cues)** | **Partially Supports** | The map root provides resource-backed default help text for keyboard navigation, the authentication `InfoBar` presents explanatory text, and attribution links have names. | Only English resources are currently provided, and application-authored interactive elements have no naming contract. Add translations and map-element naming guidance. |
| **[3.3.7 Redundant Entry (Level A, WCAG 2.2 only)](https://www.w3.org/TR/WCAG22/#redundant-entry)** | **Not Evaluated** | The control has no ordinary data-entry workflow, but authentication configuration and future interactive map content have not been assessed against this criterion. | Evaluate applicability and document host-application responsibility. |
| **[4.1.1 Parsing (Level A)](http://www.w3.org/TR/WCAG20/#ensure-compat-parses)** | **Not Applicable** | This is a compiled native WinUI control, not markup delivered to a user agent. XAML template validity is enforced by the build. | Host application markup remains application responsibility. |
| **[4.1.2 Name, Role, Value (Level A)](http://www.w3.org/TR/WCAG20/#ensure-compat-rsv)** | **Supports** | A public sealed `MapControlAutomationPeer` reports class name `MapControl`, supplies default name, help, and viewport-description text while preserving application-provided automation properties, and exposes Scroll, Transform, and Transform2 patterns with live camera values and operations. Attribution and hyperlinks expose native automation peers. | Geographic features and application-authored map elements are not yet exposed as semantic automation children. Add bounded virtual children and validate their values and lifecycle with assistive technology. |

## Level AA

| Criterion | Status | Current evidence and scope | Gap or planned action |
| --- | --- | --- | --- |
| **[1.2.4 Captions (Live) (Level AA)](http://www.w3.org/TR/WCAG20/#media-equiv-real-time-captions)** | **Not Applicable** | The control has no built-in live audio. | Host application responsibility. |
| **[1.2.5 Audio Description (Prerecorded) (Level AA)](http://www.w3.org/TR/WCAG20/#media-equiv-audio-desc-only)** | **Not Applicable** | The control has no built-in prerecorded video. | Host application responsibility. |
| **[1.3.4 Orientation (Level AA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#orientation)** | **Supports** | The control has no portrait/landscape restriction and lays out from its assigned size. | Validate representative narrow, wide, rotated-device, and resized-window layouts in the accessibility test matrix. |
| **[1.3.5 Identify Input Purpose (Level AA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#identify-input-purpose)** | **Not Applicable** | The control contains no fields collecting user information. | Host application responsibility. |
| **[1.4.3 Contrast (Minimum) (Level AA)](http://www.w3.org/TR/WCAG20/#visual-audio-contrast-contrast)** | **Not Evaluated** | High-contrast Azure styles exist, attribution uses black on an opaque white background, and the authentication message uses WinUI theme resources. | No contrast measurement covers every built-in style, label, attribution state, focus state, or composited background. Add dynamic high-contrast behavior and complete contrast measurements. |
| **[1.4.4 Resize Text (Level AA)](http://www.w3.org/TR/WCAG20/#visual-audio-contrast-scale)** | **Partially Supports** | `UISettings.TextScaleFactor` changes are monitored and applied to vector labels when `IsTextScaleFactorEnabled` is true; XAML attribution participates in platform text scaling. | The full 200% experience, collision behavior, clipping, custom icons, authentication text, and generated accessibility text have not been assessed. Validate them across supported cultures and scale settings. |
| **[1.4.5 Images of Text (Level AA)](http://www.w3.org/TR/WCAG20/#visual-audio-contrast-text-presentation)** | **Partially Supports** | Azure vector labels are rendered from text properties and scale with the system text factor rather than arriving as fixed raster imagery. | Raster tiles and custom icon imagery can contain text without an alternative, and the control cannot detect it. Provide map-element and layer descriptions; content authors remain responsible. |
| **[1.4.10 Reflow (Level AA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#reflow)** | **Not Evaluated** | The renderer responds to control size changes, and attribution wraps within a maximum width. | No 400% zoom or equivalent narrow-viewport assessment proves two-dimensional scrolling is avoided where applicable. Add resize and display-scale coverage. |
| **[1.4.11 Non-text Contrast (Level AA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#non-text-contrast)** | **Not Evaluated** | A visible focus outline uses WinUI theme resources, and high-contrast map styles are available. | Map controls, focus indicators over varied imagery, geographic boundaries, and custom elements have not been measured. Add dynamic high-contrast behavior and perform those measurements. |
| **[1.4.12 Text Spacing (Level AA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#text-spacing)** | **Not Evaluated** | Attribution is ordinary wrapping XAML text; vector labels are renderer-owned glyphs. | The control exposes no character, word, line, or paragraph spacing contract for vector labels, and no override audit has been run. Evaluate applicability and clipping with targeted tests. |
| **[1.4.13 Content on Hover or Focus (Level AA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#content-on-hover-or-focus)** | **Not Applicable** | The control has no built-in tooltip, popup, or transient content triggered solely by hover or keyboard focus. | Re-evaluate if accessible feature popups become built-in. |
| **[2.4.5 Multiple Ways (Level AA)](http://www.w3.org/TR/WCAG20/#navigation-mechanisms-mult-loc)** | **Not Applicable** | Locating pages within a set of pages is outside this control's scope. | Host application responsibility. |
| **[2.4.6 Headings and Labels (Level AA)](http://www.w3.org/TR/WCAG20/#navigation-mechanisms-descriptive)** | **Partially Supports** | The map root has resource-backed default accessible name and help text, attribution hyperlinks expose their visible label, and the authentication surface uses a native `InfoBar`. | Application-authored map elements have no accessible-label contract, and only English built-in resources are currently provided. |
| **[2.4.7 Focus Visible (Level AA)](http://www.w3.org/TR/WCAG20/#navigation-mechanisms-focus-visible)** | **Supports** | Keyboard focus activates a dedicated two-pixel theme-resource focus outline; pointer focus intentionally uses a separate state. | Measure contrast over all map imagery and Windows contrast themes. |
| **[2.4.11 Focus Not Obscured (Minimum) (Level AA, WCAG 2.2 only)](https://www.w3.org/TR/WCAG22/#focus-not-obscured-minimum)** | **Not Evaluated** | The map root has a visible focus outline, but no targeted test covers every overlay, authentication state, attribution state, or host clipping scenario. | Add focused visual tests for built-in overlays and document host responsibility. |
| **[2.5.7 Dragging Movements (Level AA, WCAG 2.2 only)](https://www.w3.org/TR/WCAG22/#dragging-movements)** | **Not Evaluated** | Keyboard alternatives exist for map navigation, but application-authored draggable map content and every built-in drag path have not been evaluated against the criterion. | Perform a criterion-specific review and add tests for equivalent non-drag operation. |
| **[2.5.8 Target Size (Minimum) (Level AA, WCAG 2.2 only)](https://www.w3.org/TR/WCAG22/#target-size-minimum)** | **Not Evaluated** | The map surface is a large input target, but attribution links, authentication actions, and future map-element targets have not been measured. | Measure all built-in interactive targets and document application-authored content requirements. |
| **[3.1.2 Language of Parts (Level AA)](http://www.w3.org/TR/WCAG20/#meaning-other-lang-id)** | **Not Evaluated** | Azure raster and vector tile requests use an explicitly selected map language, vector labels resolve the returned localized text, and automation descriptions use string resources. Language changes replace the Azure acquisition session so differently localized tiles are not mixed; an unset language uses Azure's default. | Mixed-language metadata, provider attribution text, translated sentence construction, and right-to-left behavior have not been evaluated. |
| **[3.2.3 Consistent Navigation (Level AA)](http://www.w3.org/TR/WCAG20/#consistent-behavior-consistent-locations)** | **Not Applicable** | Repeated page-level navigation mechanisms are outside this control's scope. | Host application responsibility. |
| **[3.2.4 Consistent Identification (Level AA)](http://www.w3.org/TR/WCAG20/#consistent-behavior-consistent-functionality)** | **Not Applicable** | Cross-page component identification is a host-application concern. | Keep future built-in controls and virtual peers consistently named, then re-evaluate. |
| **[3.3.3 Error Suggestion (Level AA)](http://www.w3.org/TR/WCAG20/#minimize-error-suggestions)** | **Partially Supports** | The accessible Azure authentication message explains whether a token is missing or rejected and tells the user how to correct it. | End-to-end screen-reader behavior has not been evaluated, and no accessible remediation contract exists for custom layer failures. |
| **[3.3.4 Error Prevention (Legal, Financial, Data) (Level AA)](http://www.w3.org/TR/WCAG20/#minimize-error-reversible)** | **Not Applicable** | The control does not submit legal commitments, financial transactions, or user-controlled data changes. | Host application responsibility. |
| **[3.3.8 Accessible Authentication (Minimum) (Level AA, WCAG 2.2 only)](https://www.w3.org/TR/WCAG22/#accessible-authentication-minimum)** | **Not Evaluated** | The map displays accessible missing- and invalid-token errors, but token entry occurs outside the control and has not been evaluated as an authentication process under this criterion. | Determine applicability and document the boundary between the control and host authentication configuration. |
| **[4.1.3 Status Messages (Level AA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#status-messages)** | **Partially Supports** | Attribution and settled viewport-description changes are exposed through live regions and explicit UI Automation notifications. Authentication errors use assertive live-region semantics. | End-to-end behavior with supported screen readers and loading outcomes has not been evaluated. |

## Level AAA

| Criterion | Status | Current evidence and scope | Gap or planned action |
| --- | --- | --- | --- |
| **[1.2.6 Sign Language (Prerecorded) (Level AAA)](http://www.w3.org/TR/WCAG20/#media-equiv-sign)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate if prerecorded synchronized media becomes built-in. |
| **[1.2.7 Extended Audio Description (Prerecorded) (Level AAA)](http://www.w3.org/TR/WCAG20/#media-equiv-extended-ad)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate if prerecorded video becomes built-in. |
| **[1.2.8 Media Alternative (Prerecorded) (Level AAA)](http://www.w3.org/TR/WCAG20/#media-equiv-text-doc)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate if prerecorded synchronized media becomes built-in. |
| **[1.2.9 Audio-only (Live) (Level AAA)](http://www.w3.org/TR/WCAG20/#media-equiv-live-audio-only)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate if live audio becomes built-in. |
| **[1.3.6 Identify Purpose (Level AAA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#identify-purpose)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Perform a criterion-specific semantic-purpose review. |
| **[1.4.6 Contrast (Enhanced) (Level AAA)](http://www.w3.org/TR/WCAG20/#visual-audio-contrast7)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Measure applicable built-in text and rendered labels. |
| **[1.4.7 Low or No Background Audio (Level AAA)](http://www.w3.org/TR/WCAG20/#visual-audio-contrast-noaudio)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate if audio becomes built-in. |
| **[1.4.8 Visual Presentation (Level AAA)](http://www.w3.org/TR/WCAG20/#visual-audio-contrast-visual-presentation)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Perform a criterion-specific visual presentation review. |
| **[1.4.9 Images of Text (No Exception) (Level AAA)](http://www.w3.org/TR/WCAG20/#visual-audio-contrast-text-images)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate raster tiles, custom imagery, and rendered labels. |
| **[2.1.3 Keyboard (No Exception) (Level AAA)](http://www.w3.org/TR/WCAG20/#keyboard-operation-all-funcs)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate every built-in and application-extension input path. |
| **[2.2.3 No Timing (Level AAA)](http://www.w3.org/TR/WCAG20/#time-limits-no-exceptions)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Perform a criterion-specific timing review. |
| **[2.2.4 Interruptions (Level AAA)](http://www.w3.org/TR/WCAG20/#time-limits-postponed)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate future automatic updates and notifications. |
| **[2.2.5 Re-authenticating (Level AAA)](http://www.w3.org/TR/WCAG20/#time-limits-server-timeout)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Determine applicability to host-supplied Azure authentication. |
| **[2.2.6 Timeouts (Level AAA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#timeouts)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate network and authentication timeout behavior. |
| **[2.3.2 Three Flashes (Level AAA)](http://www.w3.org/TR/WCAG20/#seizure-three-times)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Perform a criterion-specific visual review. |
| **[2.3.3 Animation from Interactions (Level AAA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#animation-from-interactions)** | **Supports** | `MapControl` honors `UISettings.AnimationsEnabled` at load and runtime by suppressing programmatic and quick-keyboard interpolation, explicit animation requests, touch translation inertia, focus transitions, and raster/vector tile fades while preserving direct touch response. | Application-authored animations and content inside custom map elements remain application responsibility. |
| **[2.4.8 Location (Level AAA)](http://www.w3.org/TR/WCAG20/#navigation-mechanisms-location)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Determine applicability to a reusable map control. |
| **[2.4.9 Link Purpose (Link Only) (Level AAA)](http://www.w3.org/TR/WCAG20/#navigation-mechanisms-link)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate provider attribution links without surrounding context. |
| **[2.4.10 Section Headings (Level AAA)](http://www.w3.org/TR/WCAG20/#navigation-mechanisms-headings)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Determine applicability to control-owned content. |
| **[2.4.12 Focus Not Obscured (Enhanced) (Level AAA, WCAG 2.2 only)](https://www.w3.org/TR/WCAG22/#focus-not-obscured-enhanced)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Test complete focus visibility across all built-in overlays and host layouts. |
| **[2.4.13 Focus Appearance (Level AAA, WCAG 2.2 only)](https://www.w3.org/TR/WCAG22/#focus-appearance)** | **Partially Supports** | Keyboard focus displays a two-DIP border around the full map perimeter, satisfying the criterion's minimum indicator-area shape in the default template. | Verify that the theme-resource border maintains at least 3:1 contrast against the same pixels in the unfocused map across imagery, themes, and forced contrast before marking **Supports**. |
| **[2.5.5 Target Size (Level AAA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#target-size)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Measure built-in targets and define application-authored target guidance. |
| **[2.5.6 Concurrent Input Mechanisms (Level AAA, WCAG 2.1 and 2.2)](https://www.w3.org/TR/WCAG21/#concurrent-input-mechanisms)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Test switching among keyboard, mouse, pen, touch, and assistive input. |
| **[3.1.3 Unusual Words (Level AAA)](http://www.w3.org/TR/WCAG20/#meaning-idioms)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate generated descriptions and provider terminology. |
| **[3.1.4 Abbreviations (Level AAA)](http://www.w3.org/TR/WCAG20/#meaning-located)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate generated descriptions and provider attribution. |
| **[3.1.5 Reading Level (Level AAA)](http://www.w3.org/TR/WCAG20/#meaning-supplements)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate generated descriptions and help text. |
| **[3.1.6 Pronunciation (Level AAA)](http://www.w3.org/TR/WCAG20/#meaning-pronunciation)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate localized geographic names and generated descriptions. |
| **[3.2.5 Change on Request (Level AAA)](http://www.w3.org/TR/WCAG20/#consistent-behavior-no-extreme-changes-context)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Perform a criterion-specific interaction review. |
| **[3.3.5 Help (Level AAA)](http://www.w3.org/TR/WCAG20/#minimize-error-context-help)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Evaluate keyboard help, authentication guidance, and host integration. |
| **[3.3.6 Error Prevention (All) (Level AAA)](http://www.w3.org/TR/WCAG20/#minimize-error-reversible-all)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Determine applicability to control-owned actions and configuration. |
| **[3.3.9 Accessible Authentication (Enhanced) (Level AAA, WCAG 2.2 only)](https://www.w3.org/TR/WCAG22/#accessible-authentication-enhanced)** | **Not Evaluated** | Level AAA is outside the current assessment scope. | Determine applicability to host-supplied Azure authentication. |

## References

- [Web Content Accessibility Guidelines (WCAG) 2.0](https://www.w3.org/TR/WCAG20/)
- [Web Content Accessibility Guidelines (WCAG) 2.1](https://www.w3.org/TR/WCAG21/)
- [Web Content Accessibility Guidelines (WCAG) 2.2](https://www.w3.org/TR/WCAG22/)
- [Microsoft Accessibility Conformance Reports](https://www.microsoft.com/accessibility/conformance-reports)
- [Azure Maps accessibility](https://learn.microsoft.com/azure/azure-maps/map-accessibility)
- [UWP `MapControlAutomationPeer`](https://learn.microsoft.com/uwp/api/windows.ui.xaml.automation.peers.mapcontrolautomationpeer)
