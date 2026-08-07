#requires -Version 5.1
# V1 data verification for assets/data/structural/sections.json (self-consistency layer).
# Checks every row against relations that must hold by definition, catching typos and
# transposed digits without any external source:
#   Wely ~= 2*Iy/h      (elastic modulus IS I/c; catalog rounding only)
#   iy   ~= sqrt(Iy/A)  (radius of gyration definition)
#   iz   ~= sqrt(Iz/A)
#   m    ~= A * 0.785   (steel 7850 kg/m3 -> kg/m per cm2)
# Tolerances are catalog-rounding tolerances (field-differentiated; J/Iw excluded — the
# shipped values are fillet-less analytic and flagged as such in the file meta).
[CmdletBinding()]
param([string]$SectionsPath = 'assets\data\structural\sections.json')

$ErrorActionPreference = 'Stop'
$doc = Get-Content -LiteralPath $SectionsPath -Raw | ConvertFrom-Json
$failures = 0
$checked = 0

function Check($name, $label, $actual, $expected, $tolFrac) {
    $script:checked++
    if ($expected -eq 0) { return }
    $dev = [Math]::Abs(($actual - $expected) / $expected)
    if ($dev -gt $tolFrac) {
        $script:failures++
        Write-Output ("FAIL {0,-8} {1,-12} listed={2} derived={3:G5} dev={4:P2} (tol {5:P1})" -f $name, $label, $actual, $expected, $dev, $tolFrac)
    }
}

foreach ($s in $doc.sections) {
    # h in mm, Iy in cm4 -> Wel = 2*Iy/(h/10) cm3
    Check $s.name 'Wely=2Iy/h' $s.Wely (2.0 * $s.Iy / ($s.h / 10.0)) 0.015
    Check $s.name 'ry=sqrt(Iy/A)' $s.ry ([Math]::Sqrt($s.Iy / $s.A)) 0.015
    Check $s.name 'rz=sqrt(Iz/A)' $s.rz ([Math]::Sqrt($s.Iz / $s.A)) 0.015
    Check $s.name 'm=A*0.785' $s.m ($s.A * 0.785) 0.02
    if ($s.Wply -le $s.Wely) { $failures++; Write-Output "FAIL $($s.name) Wply<=Wely (plastic must exceed elastic)" }
    if ($s.Wplz -le $s.Welz) { $failures++; Write-Output "FAIL $($s.name) Wplz<=Welz" }
    if ($s.Iy -le $s.Iz) { $failures++; Write-Output "FAIL $($s.name) Iy<=Iz (strong axis must dominate)" }
}

Write-Output ("checked {0} relations across {1} sections: {2}" -f $checked, $doc.sections.Count, $(if ($failures -eq 0) { 'ALL PASS' } else { "$failures FAILURES" }))
if ($failures -gt 0) { exit 1 }
