project(
  'gif320sharp-gegl',
  'c',
  version: '0.1.0',
  default_options: [
    'c_std=c11',
    'warning_level=2',
  ],
)

gegl = dependency('gegl-0.4')
babl = dependency('babl-0.1')
gmodule = dependency('gmodule-2.0')
cc = meson.get_compiler('c')
math_dep = cc.find_library('m', required: false)

preview_sources = [
  'src/gif320_vt320_preview.c',
  'src/gif320_vt320_core.c',
]

second_pass_sources = [
  'src/gif320_vt320_second_pass.c',
  'src/gif320_vt320_core.c',
]

foreach module : [
  ['gif320sharp-vt320-preview', preview_sources],
  ['gif320sharp-vt320-second-pass', second_pass_sources],
]
shared_module(
  module[0],
  module[1],
  include_directories: include_directories('src'),
  dependencies: [gegl, babl, gmodule, math_dep],
  install: true,
  install_dir: get_option('libdir') / 'gegl-0.4',
)
endforeach

manual_atlas_regression = executable(
  'manual-atlas-regression',
  [
    'test/manual_atlas_regression.c',
    'src/gif320_vt320_core.c',
  ],
  include_directories: include_directories('src'),
  dependencies: [math_dep],
)

test('manual atlas regression', manual_atlas_regression)
