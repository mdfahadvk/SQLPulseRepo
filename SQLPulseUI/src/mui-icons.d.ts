// The installed @mui/icons-material build ships JavaScript only (no bundled
// .d.ts files), so deep imports such as `@mui/icons-material/Refresh` would
// otherwise fail with TS7016. This ambient declaration types them as SvgIcon
// components.
//
// Trade-off: because icon names are not statically checked, a typo or an icon
// that is missing from this package build only fails at bundle time
// ("Rollup failed to resolve import"). Verify an icon exists in
// node_modules/@mui/icons-material before importing it.
declare module '@mui/icons-material/*' {
  import { SvgIconProps } from '@mui/material/SvgIcon';
  const Icon: React.FC<SvgIconProps>;
  export default Icon;
}
