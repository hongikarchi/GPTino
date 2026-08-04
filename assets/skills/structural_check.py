#! python 3
# Structural verdict payload: deterministic SLS deflection-limit + utilization checks on solver results. Wire verbatim; never edit per job.
#
# Vetted check code in the bake_manager.py mold: GPTino creates this as a Python 3
# component and WIRES it — the model never rewrites the verdict math (house rules).
#
# Input sockets (declare via setComponentIo, names exact, access in parens):
#   resultsJson (item, str)   solver's one-line results JSON; must contain the key named
#                             by deflectionKey below (meters, absolute value used)
#   memberKind  (item, str)   "simply_supported" | "cantilever" — picks the limit rule
#   spanM       (item, float) span L in meters (cantilever: the overhang length a)
#   deflectionKey (item, str) key in resultsJson holding max deflection, e.g.
#                             "maxDeflectionM" / "maxDisplacementM"
#   utilizations (list, float) OPTIONAL: member utilizations from Karamba Utilization
#                             (leave unwired to skip the strength check)
#   limitDivisor (item, float) OPTIONAL override of the deflection divisor; 0/unwired =
#                             use the memberKind default (250 simply supported, 125
#                             cantilever-equivalent). Only override when the user names
#                             a different code limit.
# Output sockets:
#   verdictJson (item) one-line JSON verdict (see below)
#   checked     (item) assigned True ONLY when the checks ran to completion — mirror of
#                      the solver's solved contract so a skipped run can never read green
#
# ULS load factors for reference by the caller (EC0 base case): gamma_G = 1.35,
# gamma_Q = 1.5. Utilizations fed in MUST come from a factored (ULS) analysis;
# the deflection in resultsJson MUST come from an unfactored (SLS) analysis.

import json

_DIVISORS = {"simply_supported": 250.0, "cantilever": 125.0}

rj = resultsJson if "resultsJson" in dir() else None
kind = str(memberKind).strip().lower() if "memberKind" in dir() and memberKind else None
span = float(spanM) if "spanM" in dir() and spanM else None
key = str(deflectionKey).strip() if "deflectionKey" in dir() and deflectionKey else None
utils = list(utilizations) if "utilizations" in dir() and utilizations else None
override = float(limitDivisor) if "limitDivisor" in dir() and limitDivisor else 0.0

if rj and kind in _DIVISORS and span and span > 0 and key:
    data = json.loads(str(rj))
    if key not in data:
        verdictJson = json.dumps({"error": "deflection key '%s' not in resultsJson" % key})
    else:
        defl = abs(float(data[key]))
        divisor = override if override > 0 else _DIVISORS[kind]
        limit = span / divisor
        d_pass = defl <= limit
        verdict = {
            "passes": bool(d_pass),
            "deflection": {
                "valueM": defl,
                "limitM": limit,
                "rule": "L/%g (%s)" % (divisor, kind),
                "ratio": (defl / limit) if limit > 0 else None,
                "pass": bool(d_pass),
            },
        }
        if utils is not None:
            u_max = max(abs(float(u)) for u in utils) if len(utils) > 0 else None
            u_pass = (u_max is not None) and (u_max <= 1.0)
            verdict["utilization"] = {
                "max": u_max,
                "pass": bool(u_pass),
                "note": "must come from ULS-factored analysis (1.35G + 1.5Q)",
            }
            verdict["passes"] = bool(d_pass and u_pass)
        verdictJson = json.dumps(verdict)
        checked = True
# unwired/incomplete inputs: assign nothing but a status note (no 'checked' output)
else:
    verdictJson = json.dumps({"waiting": "inputs incomplete (resultsJson/memberKind/spanM/deflectionKey)"})
