// Paths of the machine of the daemon, as the API gives them: '/' separates the folders on every platform, so a drive
// of Windows, which is for development only, reads `C:/`.

/**
 * The folder that holds a path, or null for a root (`/`, `C:/`).
 */
export function parentPath (path: string): string | null {
  const trimmed = path.replace(/\/+$/, '')
  const index = trimmed.lastIndexOf('/')
  if (index === -1) {
    return null
  }
  const parent = trimmed.slice(0, index)
  if (parent === '') {
    return '/'
  }
  return /^[A-Z]:$/i.test(parent) ? `${parent}/` : parent
}

/**
 * A path inside a folder.
 */
export function joinPath (directory: string, name: string): string {
  return directory.endsWith('/') ? directory + name : `${directory}/${name}`
}

/**
 * Whether a name can be given to a new folder: not empty, without '/', and not `.` or `..`.
 */
export function isValidFolderName (name: string): boolean {
  return name.length > 0
    && name.trim() === name
    && name !== '.'
    && name !== '..'
    && !name.includes('/')
}

/**
 * The last segment of a path, or the path itself for a root.
 */
export function pathName (path: string): string {
  const trimmed = path.replace(/\/+$/, '')
  const index = trimmed.lastIndexOf('/')
  return index === -1 ? path : trimmed.slice(index + 1)
}
